using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;

namespace StS2Sim;

/// <summary>
/// One trial = run N turns of "fill hand → play cards via policy → end turn".
/// Each trial spins up a fresh combat through <see cref="Harness"/> and tears
/// it down at the end. The dummy is healed back to full each turn so combat
/// never ends naturally — we measure damage dealt, not survivability.
/// </summary>
internal sealed class DamagePerTurnSim
{
    public required string DeckName { get; init; }
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public int HandSize { get; init; } = 5;
    public int Energy { get; init; } = 3;
    public int Turns { get; init; } = 5;
    public IPlayPolicy Policy { get; init; } = new GreedyAttackPolicy();
    public uint? PolicyRngSeed { get; init; } = null;

    public sealed record TurnResult(int Turn, int Damage, IReadOnlyList<string> Hand, IReadOnlyList<string> CardsPlayed);

    public sealed record TrialResult(uint Seed, IReadOnlyList<TurnResult> Turns)
    {
        public int TotalDamage => Turns.Sum(t => t.Damage);
        public double AvgPerTurn => Turns.Count == 0 ? 0 : (double)TotalDamage / Turns.Count;
    }

    public async Task<IReadOnlyList<TrialResult>> RunTrials(int trials)
    {
        var results = new List<TrialResult>();
        for (int t = 0; t < trials; t++)
        {
            var seed = (uint)(0xC0FFEE + t);
            results.Add(await RunSingleTrial(seed));
        }
        return results;
    }

    public async Task<TrialResult> RunSingleTrial(uint shuffleSeed)
    {
        var harness = Harness.BeginCombat<Ironclad>(deckOverride: Deck, shuffleSeed: shuffleSeed);
        var policyRng = new Random((int)(PolicyRngSeed ?? shuffleSeed ^ 0xDEAD_BEEFu));
        try
        {
            var turnResults = new List<TurnResult>();
            for (int turn = 0; turn < Turns; turn++)
            {
                turnResults.Add(await RunSingleTurn(harness, turn + 1, policyRng));
            }
            return new TrialResult(shuffleSeed, turnResults);
        }
        finally
        {
            Harness.EndCombat();
        }
    }

    private async Task<TurnResult> RunSingleTurn(Harness.CombatHarness harness, int roundNumber, Random policyRng)
    {
        var pcs = harness.Player.PlayerCombatState!;
        var hand = pcs.Hand;
        var discard = pcs.DiscardPile;

        // Bump RoundNumber so Creature.AfterTurnStart's `roundNumber > 1`
        // gate fires ClearBlock on turns 2+ (matches real combat).
        harness.State.RoundNumber = roundNumber;

        // Snapshot every power's Amount as AmountOnTurnStart. Some powers
        // (Strength variants, Energized) compare current Amount to this
        // baseline; without it the comparison is meaningless.
        foreach (var c in harness.State.Creatures)
            c.BeforeTurnStart(roundNumber, CombatSide.Player);

        // Calls ClearBlock() when roundNumber > 1, so player.Block resets
        // each turn instead of accumulating across turns.
        foreach (var c in harness.State.Creatures)
            await c.AfterTurnStart(roundNumber, CombatSide.Player);

        var hpBefore = harness.Dummy.CurrentHp;

        // Reset PCS.Energy at turn start. PCS.Energy is the source of truth —
        // cards like Offering / Bloodletting modify it via PlayerCmd.GainEnergy /
        // LoseEnergy, and we read it back to give the policy an accurate budget.
        Reflect.SetEnergy(pcs, Energy);

        // Reset CapturedXValue on all hand cards so X-cost cards (Havoc-style)
        // re-capture cleanly each play.
        foreach (var c in hand.Cards)
        {
            if (c.EnergyCost.CostsX) c.EnergyCost.CapturedXValue = 0;
        }

        var played = new List<string>();
        // Capture autoplays (Hellraiser drawn-strike, Havoc, etc.) into the
        // same list so the turn log matches reality. Capture must be installed
        // BEFORE the draw because Hellraiser fires mid-draw.
        PlayCapture.Start(played);

        // Use CardPileCmd.Draw so Hook.AfterCardDrawn fires — that's what powers
        // like Hellraiser hook into to autoplay Strikes mid-draw.
        int needed = HandSize - hand.Cards.Count;
        if (needed > 0)
            await CardPileCmd.Draw(harness.Ctx, needed, harness.Player);

        // Snapshot hand AFTER autoplays have resolved (those cards aren't
        // really "drawn into hand" from the player's perspective — they were
        // drawn and immediately played out).
        var handSnapshot = hand.Cards.Select(CardLabels.Format).ToList();

        await PlayPhase(harness, pcs, played, policyRng);

        PlayCapture.Stop();
        var hpAfter = harness.Dummy.CurrentHp;

        // End-of-turn: dump hand to discard.
        foreach (var c in hand.Cards.ToList())
        {
            hand.RemoveInternal(c);
            discard.AddInternal(c);
        }

        // Fire end-of-turn hooks for both sides so power durations
        // (Vulnerable, Weak, Frail, ...) tick down as in real combat.
        await TurnHooks.FireAfterTurnEnd(harness, CombatSide.Player);
        await TurnHooks.FireAfterTurnEnd(harness, CombatSide.Enemy);

        // Heal dummy back to full so HP doesn't ever hit 0.
        Reflect.HealToFull(harness.Dummy);

        return new TurnResult(roundNumber, hpBefore - hpAfter, handSnapshot, played);
    }

    private async Task PlayPhase(Harness.CombatHarness harness, MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState pcs, List<string> played, Random policyRng)
    {
        while (pcs.Energy > 0)
        {
            var card = Policy.ChooseCard(harness, pcs.Energy, policyRng);
            if (card == null) break;

            var cost = card.EnergyCost.GetResolved();
            if (cost > pcs.Energy) break; // policy lied; bail

            var resources = new ResourceInfo
            {
                EnergySpent = cost,
                EnergyValue = cost,
                StarsSpent = 0,
                StarValue = 0,
            };
            var target = card.TargetType == TargetType.Self
                ? harness.Player.Creature
                : harness.Dummy;
            // OnPlayWrapper does NOT auto-debit PCS.Energy — that's done by
            // SpendResources upstream of the play action. So we deduct the cost
            // ourselves; cards that gain energy mid-OnPlay (Offering, Bloodletting)
            // will be visible in the next iter.
            pcs.LoseEnergy(cost);
            await card.OnPlayWrapper(harness.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
            played.Add(CardLabels.Format(card));
        }
    }
}
