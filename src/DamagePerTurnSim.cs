using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

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
        var harness = Harness.BeginCombat<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(
            deckOverride: Deck,
            shuffleSeed: shuffleSeed);
        var policyRng = new Random((int)(PolicyRngSeed ?? shuffleSeed ^ 0xDEAD_BEEFu));
        try
        {
            var hand = harness.Player.PlayerCombatState!.Hand;
            var draw = harness.Player.PlayerCombatState!.DrawPile;
            var discard = harness.Player.PlayerCombatState!.DiscardPile;

            var turnResults = new List<TurnResult>();

            for (int turn = 0; turn < Turns; turn++)
            {
                // Bump RoundNumber so Creature.AfterTurnStart's `roundNumber > 1`
                // gate fires ClearBlock on turns 2+ (matches real combat).
                harness.State.RoundNumber = turn + 1;

                // Snapshot every power's Amount as AmountOnTurnStart. Some powers
                // (Strength variants, Energized) compare current Amount to this
                // baseline; without it the comparison is meaningless.
                foreach (var c in harness.State.Creatures)
                {
                    c.BeforeTurnStart(harness.State.RoundNumber, MegaCrit.Sts2.Core.Combat.CombatSide.Player);
                }
                // Calls ClearBlock() when roundNumber > 1, so player.Block resets
                // each turn instead of accumulating across turns.
                foreach (var c in harness.State.Creatures)
                {
                    await c.AfterTurnStart(harness.State.RoundNumber, MegaCrit.Sts2.Core.Combat.CombatSide.Player);
                }

                var hpBefore = harness.Dummy.CurrentHp;
                var pcs = harness.Player.PlayerCombatState!;
                // Reset PCS.Energy = Energy at turn start. PCS.Energy is the source
                // of truth — cards like Offering / Bloodletting modify it via
                // PlayerCmd.GainEnergy/LoseEnergy, and we read it back to give
                // the policy an accurate budget.
                SetEnergy(pcs, Energy);

                // Reset CapturedXValue on all hand cards so X-cost cards
                // (Havoc-style) re-capture cleanly each play.
                foreach (var c in pcs.Hand.Cards)
                {
                    if (c.EnergyCost.CostsX) c.EnergyCost.CapturedXValue = 0;
                }
                var played = new List<string>();
                // Capture autoplays (Hellraiser drawn-strike, Havoc, etc.) into
                // the same list so the turn log matches reality. The capture must
                // be installed BEFORE the draw because Hellraiser fires mid-draw.
                GodotShims.StartCapturingPlays(played);

                // Use CardPileCmd.Draw so Hook.AfterCardDrawn fires — that's what
                // powers like Hellraiser hook into to autoplay Strikes mid-draw.
                int needed = HandSize - hand.Cards.Count;
                if (needed > 0)
                {
                    await MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(harness.Ctx, needed, harness.Player);
                }

                // Snapshot hand AFTER autoplays have resolved (those cards aren't
                // really "drawn into hand" from the player's perspective — they
                // were drawn and immediately played out).
                var handSnapshot = hand.Cards.Select(c => CardLabel(c)).ToList();

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
                    var target = card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self
                        ? harness.Player.Creature
                        : harness.Dummy;
                    // OnPlayWrapper does NOT auto-debit PCS.Energy — that's done
                    // by SpendResources upstream of the play action. So we deduct
                    // the cost ourselves; cards that gain energy mid-OnPlay
                    // (Offering, Bloodletting) will be visible in the next iter.
                    pcs.LoseEnergy(cost);
                    await card.OnPlayWrapper(harness.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
                    played.Add(CardLabel(card));
                }

                GodotShims.StopCapturingPlays();
                var hpAfter = harness.Dummy.CurrentHp;
                turnResults.Add(new TurnResult(turn + 1, hpBefore - hpAfter, handSnapshot, played));

                // End-of-turn: dump hand to discard.
                foreach (var c in hand.Cards.ToList())
                {
                    hand.RemoveInternal(c);
                    discard.AddInternal(c);
                }

                // Fire end-of-turn hooks for both sides so power durations
                // (Vulnerable, Weak, Frail, ...) tick down as in real combat.
                await FireAfterTurnEnd(harness, MegaCrit.Sts2.Core.Combat.CombatSide.Player);
                await FireAfterTurnEnd(harness, MegaCrit.Sts2.Core.Combat.CombatSide.Enemy);

                // Heal dummy back to full so HP doesn't ever hit 0.
                ReviveDummy(harness.Dummy);
            }

            return new TrialResult(shuffleSeed, turnResults);
        }
        finally
        {
            Harness.EndCombat();
        }
    }

    // FillHand was a manual hand/draw/discard mover; replaced with
    // CardPileCmd.Draw so on-draw hooks (Hellraiser autoplay, etc.) fire.
    // CardPileCmd.Draw also handles draw->discard reshuffle internally
    // via the Shuffle shim in GodotShims.

    private static void SetEnergy(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState pcs, int amount)
    {
        var prop = typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState)
            .GetProperty("Energy", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop!.SetValue(pcs, amount);
    }

    /// <summary>"CARD.STRIKE_IRONCLAD" + upgrade level → "Strike Ironclad+"</summary>
    private static string CardLabel(MegaCrit.Sts2.Core.Models.CardModel card)
    {
        var pretty = CardIdResolver.PrettyName(card.Id.ToString());
        return card.IsUpgraded ? pretty + (card.CurrentUpgradeLevel == 1 ? "+" : "+" + card.CurrentUpgradeLevel) : pretty;
    }

    private static async Task FireAfterTurnEnd(Harness.CombatHarness h, MegaCrit.Sts2.Core.Combat.CombatSide side)
    {
        // Manually iterate listeners and call AfterTurnEnd. Avoids Hook.AfterTurnEnd's
        // LocalContext.NetId requirement (we have no netcode).
        foreach (var listener in h.State.IterateHookListeners().ToList())
        {
            await listener.AfterTurnEnd(h.Ctx, side);
        }
    }

    private static void ReviveDummy(MegaCrit.Sts2.Core.Entities.Creatures.Creature dummy)
    {
        // Push CurrentHp back to MaxHp via reflection (private setter).
        var prop = typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature)
            .GetProperty("CurrentHp", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop!.SetValue(dummy, dummy.MaxHp);
    }
}
