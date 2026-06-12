using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;

namespace StS2Sim.Advise;

/// <summary>
/// Reconstructs an in-progress live combat (described by <see cref="AdviseRequest"/>)
/// inside the headless harness, so the advisor can roll it forward with the
/// real game rules. One mirror per rollout — the unknown parts of the state
/// (draw pile ORDER, future enemy moves) vary with the trial seed, which is
/// exactly the uncertainty best-of-K sampling is for. The known parts (piles'
/// contents, HP, block, powers, this turn's telegraphed intents) are forced
/// to the live values.
///
/// Fidelity gaps are documented in AI_PLAYER.md and surfaced in
/// <see cref="MirrorResult.Notes"/> rather than hidden.
/// </summary>
internal static class StateMirror
{
    public sealed record MirrorResult(
        Harness.CombatHarness H,
        IReadOnlyList<int> LiveEnemyIndexByMirrorIndex,
        int DrawInferred,
        int DrawReported,
        IReadOnlyList<string> Notes);

    public static async Task<MirrorResult> Mirror(AdviseRequest live, uint seed)
    {
        var combat = live.Combat ?? throw new ArgumentException("request has no 'combat' block");
        var notes = new List<string>();

        var charId = live.Run?.Character ?? "";
        var charType = Harness.ResolveCharacterType(charId)
            ?? throw new ArgumentException($"Unknown character '{charId}'");

        // ── Combat deck = hand ∪ discard ∪ exhaust ∪ inferred draw. The live
        // API exposes the draw pile's count but not its contents, so the draw
        // set is deck − (hand ∪ discard ∪ exhaust) as a multiset.
        var hand = combat.Hand.Select(c => ToKey(c.Id, c.Upgraded, c.Enchantment)).ToList();
        var discard = combat.DiscardPile.Select(c => ToKey(c.Id, c.Upgraded, c.Enchantment)).ToList();
        var exhaust = combat.ExhaustPile.Select(c => ToKey(c.Id, c.Upgraded, c.Enchantment)).ToList();
        var masterDeck = (live.Deck ?? new List<AdviseRequest.CardRef>())
            .Select(c => ToKey(c.Id, c.Upgraded, c.Enchantment)).ToList();

        var inferredDraw = MultisetSubtract(masterDeck, hand.Concat(discard).Concat(exhaust));
        if (inferredDraw.Count != combat.DrawPileCount)
            notes.Add($"draw pile inferred {inferredDraw.Count} cards but live reports {combat.DrawPileCount} " +
                      "(mid-combat generated/removed cards can't be reconstructed from counts)");

        var deckOverride = new List<Harness.DeckEntry>();
        foreach (var key in hand.Concat(discard).Concat(exhaust).Concat(inferredDraw))
        {
            var type = CardIdResolver.Resolve(key.Id);
            if (type == null) { notes.Add($"unknown card id '{key.Id}' skipped"); continue; }
            deckOverride.Add(new Harness.DeckEntry(type, key.Up ? 1 : 0, key.EnchId, key.EnchAmt));
        }

        // ── Spin up the combat: real character, live relic list, live enemy
        // lineup by monster id. Combat-start hooks are deliberately NOT fired —
        // the live combat already ran them, and their products (powers, the
        // initial orb, Osty) arrive via the mirrored state instead.
        var liveEnemies = combat.Enemies.Where(e => e.IsAlive).ToList();
        if (liveEnemies.Count == 0) throw new ArgumentException("no living enemies to mirror");

        var h = Harness.BeginCombat(
            charType,
            deckOverride,
            shuffleSeed: seed,
            relicIds: live.Relics?.Select(r => r.Id ?? "").Where(s => s.Length > 0).ToList(),
            monsterIds: liveEnemies.Select(e => e.Id ?? "").ToList());

        var pcs = h.Player.PlayerCombatState!;

        // ── Piles: pull the listed hand/discard/exhaust cards out of the
        // shuffled draw pile by identity; what remains IS the draw pile.
        foreach (var key in hand)
            MoveTo(h, key, c => { pcs.DrawPile.RemoveInternal(c); pcs.Hand.AddInternal(c); }, notes, "hand");
        foreach (var key in discard)
            MoveTo(h, key, c => { pcs.DrawPile.RemoveInternal(c); pcs.DiscardPile.AddInternal(c); }, notes, "discard");
        foreach (var key in exhaust)
            MoveTo(h, key, c => { pcs.DrawPile.RemoveInternal(c); pcs.ExhaustPile.AddInternal(c); }, notes, "exhaust");

        // ── Player vitals + resources. RoundNumber matters: block-clear and
        // "first turn" gates key off it in later rollout turns.
        h.State.RoundNumber = Math.Max(1, combat.Turn);
        if (live.Player is { } p)
        {
            if (p.MaxHp > 0) Reflect.SetMaxHp(h.Player.Creature, p.MaxHp);
            if (p.Hp > 0) Reflect.SetCurrentHp(h.Player.Creature, p.Hp);
            Reflect.SetBlock(h.Player.Creature, Math.Max(0, p.Block));
        }
        Reflect.SetEnergy(pcs, Math.Max(0, combat.Energy));
        foreach (var pw in combat.PlayerPowers)
            await ApplyPowerExact(h.Player.Creature, pw, notes);

        // ── Enemies: vitals, powers, and this turn's telegraphed move.
        var liveIndexByMirror = new List<int>();
        for (int i = 0; i < liveEnemies.Count; i++)
        {
            var liveE = liveEnemies[i];
            var creature = h.Enemies[i];
            liveIndexByMirror.Add(liveE.EnemyIndex);

            if (liveE.MaxHp > 0) Reflect.SetMaxHp(creature, liveE.MaxHp);
            if (liveE.Hp > 0) Reflect.SetCurrentHp(creature, liveE.Hp);
            Reflect.SetBlock(creature, Math.Max(0, liveE.Block));
            foreach (var pw in liveE.Powers)
                await ApplyPowerExact(creature, pw, notes);

            ForceIntent(creature, liveE.Intent, notes);
        }

        return new MirrorResult(h, liveIndexByMirror, inferredDraw.Count, combat.DrawPileCount, notes);
    }

    /// <summary>
    /// Pin an enemy's next move to the live telegraphed intent via the game's
    /// own SetMoveImmediate. Unknown/missing intent falls back to a seeded
    /// roll — degraded but honest (the rollout still faces a legal move).
    /// </summary>
    private static void ForceIntent(Creature creature, string? intentId, List<string> notes)
    {
        var monster = creature.Monster;
        if (monster == null) return;

        if (!string.IsNullOrEmpty(intentId)
            && monster.MoveStateMachine is { } machine
            && machine.States.TryGetValue(intentId, out var state)
            && state is MoveState move)
        {
            monster.SetMoveImmediate(move, forceTransition: true);
            return;
        }

        monster.RollMove(creature.CombatState.PlayerCreatures);
        if (!string.IsNullOrEmpty(intentId))
            notes.Add($"{monster.Id}: intent '{intentId}' not found in move state machine; rolled instead");
    }

    /// <summary>
    /// Apply a power through the game's own pipeline, then force the stack
    /// count to the exact live value — apply-time hooks (strength modifiers,
    /// artifact-style preventers) could otherwise change what lands.
    /// </summary>
    private static async Task ApplyPowerExact(Creature target, AdviseRequest.PowerRef pw, List<string> notes)
    {
        if (string.IsNullOrEmpty(pw.Id) || pw.Stacks == 0) return;
        ModelId modelId;
        try { modelId = ModelId.Deserialize(pw.Id); }
        catch { notes.Add($"bad power id '{pw.Id}' skipped"); return; }

        var canonical = ModelDb.GetByIdOrNull<PowerModel>(modelId);
        if (canonical == null) { notes.Add($"unknown power id '{pw.Id}' skipped"); return; }

        var mutable = (PowerModel)canonical.ToMutable();
        await PowerCmd.Apply(mutable, target, pw.Stacks, applier: null, cardSource: null, silent: true);

        var applied = target.Powers.FirstOrDefault(x => x.Id == modelId);
        if (applied == null) notes.Add($"power '{pw.Id}' did not stick (preventer hook?)");
        else if (applied.Amount != (int)pw.Stacks) Reflect.SetPowerAmount(applied, (int)pw.Stacks);
    }

    private readonly record struct CardKey(string Id, bool Up, string? EnchId, int EnchAmt);

    private static CardKey ToKey(string? id, bool upgraded, AdviseRequest.EnchantRef? ench)
        => new(id ?? "", upgraded, ench?.Id, ench?.Amount ?? 0);

    private static void MoveTo(
        Harness.CombatHarness h, CardKey key,
        Action<MegaCrit.Sts2.Core.Models.CardModel> move, List<string> notes, string pile)
    {
        var pcs = h.Player.PlayerCombatState!;
        int wantLevel = key.Up ? 1 : 0;
        var card = pcs.DrawPile.Cards.FirstOrDefault(c =>
                c.Id.ToString() == key.Id && c.CurrentUpgradeLevel == wantLevel
                && c.Enchantment?.Id.ToString() == key.EnchId)
            ?? pcs.DrawPile.Cards.FirstOrDefault(c =>
                c.Id.ToString() == key.Id && c.CurrentUpgradeLevel == wantLevel)
            ?? pcs.DrawPile.Cards.FirstOrDefault(c => c.Id.ToString() == key.Id);
        if (card == null) { notes.Add($"could not place '{key.Id}' into {pile}"); return; }
        move(card);
    }

    private static List<CardKey> MultisetSubtract(IEnumerable<CardKey> from, IEnumerable<CardKey> remove)
    {
        var counts = new Dictionary<CardKey, int>();
        foreach (var k in from)
        {
            counts.TryGetValue(k, out var n);
            counts[k] = n + 1;
        }
        foreach (var k in remove)
        {
            if (counts.TryGetValue(k, out var n) && n > 0) counts[k] = n - 1;
        }
        var result = new List<CardKey>();
        foreach (var (key, n) in counts)
            for (int i = 0; i < n; i++) result.Add(key);
        return result;
    }
}
