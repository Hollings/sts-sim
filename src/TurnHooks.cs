using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rooms;

namespace StS2Sim;

/// <summary>
/// Manual hook firing for the headless sim. The official Hook.X paths require
/// <c>LocalContext.NetId</c> (which is netcode state we don't have), so they
/// short-circuit and return without firing listeners. We iterate listeners
/// directly to get the same effect — relics, powers, and other AbstractModels
/// register themselves through <c>CombatState.IterateHookListeners</c>.
///
/// The flow here mirrors <c>CombatManager.EndPlayerTurnPhaseOneInternal</c>,
/// the player-turn-start block in <c>CombatManager</c>, and the at-combat-start
/// (room-entered) hooks — with the minimum adaptations for headless mode (no
/// UI animations, no waiting on enemy turn).
/// </summary>
internal static class TurnHooks
{
    /// <summary>
    /// Iterate listeners and run an arbitrary hook lambda on each. The single
    /// primitive every other helper in this file delegates to.
    /// Snapshots the listener list so hooks that mutate it (powers expiring,
    /// relics being added/removed) don't corrupt the iteration.
    /// </summary>
    public static async Task FireOnAll(Harness.CombatHarness h, Func<AbstractModel, Task> action)
    {
        foreach (var listener in h.State.IterateHookListeners().ToList())
            await action(listener);
    }

    // ─── At-combat-start (relics like Vajra, Bronze Scales fire here) ────────

    /// <summary>
    /// Fire the at-combat-start hook on every listener so relics like Vajra
    /// (gives +1 Strength on combat enter) actually apply their effects. The
    /// game does this via Hook.AfterRoomEntered when entering a CombatRoom.
    /// </summary>
    public static Task FireAfterRoomEntered(Harness.CombatHarness h)
    {
        var room = new CombatRoom(h.State);
        return FireOnAll(h, l => l.AfterRoomEntered(room));
    }

    // ─── Side-turn-start (relics fire here: Bag of Marbles, Lantern, etc.) ───

    public static Task FireBeforeSideTurnStart(Harness.CombatHarness h, CombatSide side)
        => FireOnAll(h, l => l.BeforeSideTurnStart(h.Ctx, side, h.State));

    public static async Task FireAfterSideTurnStart(Harness.CombatHarness h, CombatSide side)
    {
        // Game fires both AfterSideTurnStart and AfterSideTurnStartLate.
        await FireOnAll(h, l => l.AfterSideTurnStart(side, h.State));
        await FireOnAll(h, l => l.AfterSideTurnStartLate(side, h.State));
    }

    // ─── Player-turn-start (split into two phases) ───────────────────────────

    /// <summary>
    /// First half of player-turn-start. Bumps RoundNumber, snapshots powers
    /// via BeforeTurnStart, runs ClearBlock-via-AfterTurnStart, resets energy
    /// (honoring ShouldPlayerResetEnergy), fires BeforeSideTurnStart and
    /// AfterSideTurnStart, then resets CapturedXValue on hand cards.
    ///
    /// Split from the second half so the caller can sandwich PlayCapture
    /// start/stop around just the draw + play window.
    /// </summary>
    public static async Task PrepareSideTurnStart(Harness.CombatHarness h, int roundNumber)
    {
        var pcs = h.Player.PlayerCombatState!;

        // Bump RoundNumber so Creature.AfterTurnStart's `roundNumber > 1`
        // gate fires ClearBlock on turns 2+ (matches real combat).
        h.State.RoundNumber = roundNumber;

        // Snapshot every power's Amount as AmountOnTurnStart. Some powers
        // (Strength variants, Energized) compare current Amount to this
        // baseline; without it the comparison is meaningless.
        foreach (var c in h.State.Creatures)
            c.BeforeTurnStart(roundNumber, CombatSide.Player);

        // Calls ClearBlock() when roundNumber > 1, so player.Block resets
        // each turn instead of accumulating across turns.
        foreach (var c in h.State.Creatures)
            await c.AfterTurnStart(roundNumber, CombatSide.Player);

        // Reset energy through the game's own path so MaxEnergy modifiers
        // (Coffee Dripper, Cursed Key, etc.) apply, AND so ShouldPlayerResetEnergy
        // listeners (Ice Cream, Runic Cube) can override the default reset.
        // MaxEnergy is computed via Hook.ModifyMaxEnergy on the player.
        if (Hook.ShouldPlayerResetEnergy(h.State, h.Player))
            pcs.ResetEnergy();
        else
            pcs.AddMaxEnergyToCurrent();

        // Fire BeforeSideTurnStart on all listeners (relics + powers). Bag of
        // Marbles applies turn-1 Vulnerable, Snecko Eye etc. trigger here.
        await FireBeforeSideTurnStart(h, CombatSide.Player);

        // Fire AfterSideTurnStart on all listeners. Lantern grants turn-1 energy,
        // and other start-of-turn relic/power effects fire here.
        await FireAfterSideTurnStart(h, CombatSide.Player);

        // Reset CapturedXValue on all hand cards so X-cost cards (Havoc-style)
        // re-capture cleanly each play.
        foreach (var c in pcs.Hand.Cards)
        {
            if (c.EnergyCost.CostsX) c.EnergyCost.CapturedXValue = 0;
        }
    }

    /// <summary>
    /// Second half of player-turn-start. Mirrors the game's player-turn-start
    /// hook block in <c>CombatManager</c>: AfterEnergyReset(+Late) →
    /// BeforeHandDraw(+Late) → ModifyHandDraw → AfterModifyingHandDraw →
    /// Draw (with fromHandDraw: true so AfterCardDrawn knows the source) →
    /// AfterPlayerTurnStart(Early/regular/Late).
    /// </summary>
    public static async Task PlayerTurnStartDraw(Harness.CombatHarness h, int handSize)
    {
        await FireOnAll(h, l => l.AfterEnergyReset(h.Player));
        await FireOnAll(h, l => l.AfterEnergyResetLate(h.Player));
        await FireOnAll(h, l => l.BeforeHandDraw(h.Player, h.Ctx, h.State));
        await FireOnAll(h, l => l.BeforeHandDrawLate(h.Player, h.Ctx, h.State));

        var drawCount = (int)Hook.ModifyHandDraw(h.State, h.Player, handSize, out _);
        await FireOnAll(h, l => l.AfterModifyingHandDraw());

        int needed = drawCount - h.Player.PlayerCombatState!.Hand.Cards.Count;
        if (needed > 0)
            await CardPileCmd.Draw(h.Ctx, needed, h.Player, fromHandDraw: true);

        await FireOnAll(h, l => l.AfterPlayerTurnStartEarly(h.Ctx, h.Player));
        await FireOnAll(h, l => l.AfterPlayerTurnStart(h.Ctx, h.Player));
        await FireOnAll(h, l => l.AfterPlayerTurnStartLate(h.Ctx, h.Player));
    }

    // ─── End-of-turn (the big one) ───────────────────────────────────────────

    public static async Task FireBeforeTurnEnd(Harness.CombatHarness h, CombatSide side)
    {
        // Game fires three sub-hooks in order. Powers (Vulnerable etc.) and
        // some end-of-turn relic effects key off these.
        await FireOnAll(h, l => l.BeforeTurnEndVeryEarly(h.Ctx, side));
        await FireOnAll(h, l => l.BeforeTurnEndEarly(h.Ctx, side));
        await FireOnAll(h, l => l.BeforeTurnEnd(h.Ctx, side));
    }

    public static async Task FireBeforeFlush(Harness.CombatHarness h)
    {
        await FireOnAll(h, l => l.BeforeFlush(h.Ctx, h.Player));
        await FireOnAll(h, l => l.BeforeFlushLate(h.Ctx, h.Player));
    }

    public static Task FireAfterTurnEnd(Harness.CombatHarness h, CombatSide side)
        => FireOnAll(h, l => l.AfterTurnEnd(h.Ctx, side));

    /// <summary>
    /// The full end-of-player-turn sequence, mirroring
    /// <c>CombatManager.EndPlayerTurnPhaseOneInternal</c> + the flush block.
    /// Replaces our previous manual "dump hand to discard" code which skipped
    /// every hook, every Ethereal exhaust, and every turn-end-in-hand effect.
    ///
    /// <paramref name="tickEnemySide"/>: in dummy mode (no enemy turns exist)
    /// we fire the enemy-side AfterTurnEnd here so enemy debuffs (Vulnerable)
    /// still tick once per round. In encounter mode the real enemy turn fires
    /// it — passing true there would double-tick every enemy power.
    /// </summary>
    public static async Task EndOfPlayerTurn(Harness.CombatHarness h, bool tickEnemySide = true)
    {
        await FireBeforeTurnEnd(h, CombatSide.Player);

        await DoTurnEnd(h);

        await FireBeforeFlush(h);

        await FlushHand(h);
        h.Player.PlayerCombatState!.EndOfTurnCleanup();

        await FireAfterTurnEnd(h, CombatSide.Player);
        if (tickEnemySide)
            await FireAfterTurnEnd(h, CombatSide.Enemy);
    }

    // ─── Enemy turn (encounter mode) ─────────────────────────────────────────

    /// <summary>
    /// Re-roll every living enemy's next move (their visible intent). The game
    /// does this at the start of each player turn so the player can react;
    /// firing it there also means a policy could read intents later. Safe to
    /// call on round 1: the state machine won't advance before the first move
    /// is performed.
    /// </summary>
    public static void RollEnemyMoves(Harness.CombatHarness h)
    {
        // State.Enemies, not the harness's initial list: encounters can spawn
        // minions mid-combat (Fabricator's bots) — they arrive via the game's
        // own CreatureCmd.Spawn → CombatManager.AddCreature path, and would
        // throw UnsetMove on their first TakeTurn if we never rolled for them.
        foreach (var enemy in h.State.Enemies.ToList())
        {
            if (enemy.IsAlive && enemy.Monster != null)
                enemy.PrepareForNextTurn(h.State.PlayerCreatures);
        }
    }

    /// <summary>
    /// The full enemy half-turn, mirroring CombatManager's SwitchSides →
    /// StartTurn(enemy side) → ExecuteEnemyTurn → EndEnemyTurnInternal →
    /// SwitchSides flow. <paramref name="onEnemyMove"/> fires after each
    /// enemy acts (for the event timeline). Returns early if the player dies.
    /// </summary>
    public static async Task EnemyTurn(
        Harness.CombatHarness h, int roundNumber,
        Action<MegaCrit.Sts2.Core.Entities.Creatures.Creature, string>? onEnemyMove = null)
    {
        var state = h.State;

        // Switch to enemy side. OnSideSwitch clears each monster's
        // SpawnedThisTurn flag — without it TakeTurn() skips the move.
        state.CurrentSide = CombatSide.Enemy;
        foreach (var c in state.Creatures) c.OnSideSwitch();

        // Enemy-side turn start: power snapshots, block clear, side hooks.
        var enemiesStartingTurn = state.Enemies.Where(e => e.IsAlive).ToList();
        foreach (var e in enemiesStartingTurn)
            e.BeforeTurnStart(roundNumber, CombatSide.Enemy);
        await FireBeforeSideTurnStart(h, CombatSide.Enemy);
        foreach (var e in enemiesStartingTurn)
            await e.AfterTurnStart(roundNumber, CombatSide.Enemy);
        foreach (var e in enemiesStartingTurn)
            await FireOnAll(h, l => l.AfterBlockCleared(e));
        await FireAfterSideTurnStart(h, CombatSide.Enemy);

        // Each enemy performs its rolled move through the real game pipeline.
        foreach (var enemy in state.Enemies.ToList())
        {
            if (!enemy.IsAlive || !state.ContainsCreature(enemy)) continue;
            var moveId = enemy.Monster?.NextMove.Id ?? "?";
            await enemy.TakeTurn();
            onEnemyMove?.Invoke(enemy, moveId);
            if (!h.Player.Creature.IsAlive) break;
        }

        // End of enemy turn hooks. (The real game also runs the players'
        // EndOfTurnCleanup here in EndEnemyTurnInternal; our EndOfPlayerTurn
        // already did it — no cards get played in between, so it's equivalent.)
        await FireBeforeTurnEnd(h, CombatSide.Enemy);
        await FireAfterTurnEnd(h, CombatSide.Enemy);

        // Back to the player side; RoundNumber bumps via the next
        // PrepareSideTurnStart call.
        state.CurrentSide = CombatSide.Player;
        foreach (var c in state.Creatures) c.OnSideSwitch();
    }

    // ─── Internal: per-player end-of-turn effects ────────────────────────────

    private static async Task DoTurnEnd(Harness.CombatHarness h)
    {
        var player = h.Player;
        var hand = PileType.Hand.GetPile(player);
        var discard = PileType.Discard.GetPile(player);

        // Snapshot first — exhausting and adding to play pile mutates Cards.
        var turnEndCards = new List<CardModel>();
        var etherealCards = new List<CardModel>();
        foreach (var card in hand.Cards.ToList())
        {
            if (card.HasTurnEndInHandEffect)
                turnEndCards.Add(card);
            else if (card.Keywords.Contains(CardKeyword.Ethereal) &&
                     Hook.ShouldEtherealTrigger(h.State, card))
                etherealCards.Add(card);
        }

        // Exhaust Ethereal cards (Ascender's Bane, Ghostly Wraps, etc.).
        // Without this, they sit in discard and pollute future shuffles.
        foreach (var card in etherealCards)
            await CardCmd.Exhaust(h.Ctx, card, causedByEthereal: true);

        // Trigger turn-end-in-hand effects (Reckless Charge, Bouncing Flask, etc.).
        foreach (var card in turnEndCards)
        {
            await CardPileCmd.Add(card, PileType.Play);
            await card.OnTurnEndInHand(h.Ctx);
            if (card.Keywords.Contains(CardKeyword.Ethereal))
                await CardCmd.Exhaust(h.Ctx, card, causedByEthereal: true);
            else
                await CardPileCmd.Add(card, discard);
        }
    }

    private static async Task FlushHand(Harness.CombatHarness h)
    {
        var player = h.Player;
        var hand = PileType.Hand.GetPile(player);
        var discard = PileType.Discard.GetPile(player);

        // Retention (Tactician, Well-Laid Plans, Surrounded) keeps cards in hand.
        var toDiscard = new List<CardModel>();
        var retained = new List<CardModel>();
        foreach (var card in hand.Cards.ToList())
        {
            if (card.ShouldRetainThisTurn)
                retained.Add(card);
            else
                toDiscard.Add(card);
        }

        if (Hook.ShouldFlush(h.State, player))
            await CardPileCmd.Add(toDiscard, discard);

        // Fire AfterCardRetained for cards that stayed in hand.
        foreach (var card in retained)
            await FireOnAll(h, l => l.AfterCardRetained(card));
    }
}
