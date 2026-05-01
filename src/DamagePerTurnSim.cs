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
    public required IReadOnlyList<Type> Deck { get; init; }
    public int HandSize { get; init; } = 5;
    public int Energy { get; init; } = 3;
    public int Turns { get; init; } = 5;

    public sealed record TurnResult(int Turn, int Damage, IReadOnlyList<string> CardsPlayed);

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

    private async Task<TrialResult> RunSingleTrial(uint shuffleSeed)
    {
        var harness = Harness.BeginCombat<MegaCrit.Sts2.Core.Models.Characters.Ironclad>(
            deckOverride: Deck,
            shuffleSeed: shuffleSeed);
        try
        {
            var hand = harness.Player.PlayerCombatState!.Hand;
            var draw = harness.Player.PlayerCombatState!.DrawPile;
            var discard = harness.Player.PlayerCombatState!.DiscardPile;

            var turnResults = new List<TurnResult>();

            for (int turn = 0; turn < Turns; turn++)
            {
                FillHand(hand, draw, discard, HandSize);

                var hpBefore = harness.Dummy.CurrentHp;
                int energyLeft = Energy;
                var played = new List<string>();

                // Greedy: play any attack card we can afford, left-to-right.
                bool playedAny;
                do
                {
                    playedAny = false;
                    var cards = hand.Cards.ToList();
                    foreach (var card in cards)
                    {
                        if (card.Type != CardType.Attack) continue;
                        var cost = card.EnergyCost.GetResolved();
                        if (cost > energyLeft) continue;

                        var resources = new ResourceInfo
                        {
                            EnergySpent = cost,
                            EnergyValue = cost,
                            StarsSpent = 0,
                            StarValue = 0,
                        };
                        await card.OnPlayWrapper(harness.Ctx, harness.Dummy, isAutoPlay: true, resources, skipCardPileVisuals: true);
                        energyLeft -= cost;
                        played.Add(card.Id.Entry);
                        playedAny = true;
                        break; // hand changed; restart enumeration
                    }
                }
                while (playedAny && energyLeft > 0);

                var hpAfter = harness.Dummy.CurrentHp;
                turnResults.Add(new TurnResult(turn + 1, hpBefore - hpAfter, played));

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

    private static void FillHand(CardPile hand, CardPile draw, CardPile discard, int target)
    {
        while (hand.Cards.Count < target)
        {
            if (draw.Cards.Count == 0)
            {
                if (discard.Cards.Count == 0) return;
                // Shuffle discard back into draw.
                foreach (var c in discard.Cards.ToList())
                {
                    discard.RemoveInternal(c);
                    draw.AddInternal(c);
                }
            }
            // Take top card.
            var top = draw.Cards[draw.Cards.Count - 1];
            draw.RemoveInternal(top);
            hand.AddInternal(top);
        }
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
