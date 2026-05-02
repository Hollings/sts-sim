using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace StS2Sim;

/// <summary>
/// Assertion-based smoke tests. Each test spins up an isolated combat,
/// plays specific cards, and verifies the expected state mutation.
/// Anything routed through Powers/Relics is currently a Phase-2 concern
/// and is documented in the relevant test as expected-to-fail.
/// </summary>
internal static class SmokeTests
{
    private record TestResult(string Name, bool Passed, string? Failure);

    public static async Task RunAll()
    {
        Harness.Bootstrap();
        Console.WriteLine("=== StS2 Headless Sim — Smoke Tests ===\n");

        var tests = new List<Func<Task<TestResult>>>
        {
            Test_Strike_Deals6,
            Test_TwinStrike_DealsFiveTwice,
            Test_PommelStrike_DealsNineAndDrawsOne,
            Test_Bloodletting_HpDownEnergyUp,
            Test_Defend_AddsBlock,
            Test_Feed_ExhaustsAfterPlay,
            Test_Bash_AppliesVulnerable,
            Test_StrikeIntoVulnerable_Deals9,
            Test_Inflame_BoostsLaterStrike,
            Test_BashPlus_Deals10,
            Test_StrikePlus_Deals9,
            Test_Offering_GivesEnergyAndDraw,
            Test_Hellraiser_AutoplaysDrawnStrikes,
        };

        var results = new List<TestResult>();
        foreach (var test in tests)
        {
            try
            {
                results.Add(await test());
            }
            catch (Exception ex)
            {
                var name = test.Method.Name.Replace("Test_", "");
                results.Add(new TestResult(name, false, $"threw: {ex.GetType().Name}: {ex.Message}"));
            }
        }

        Console.WriteLine();
        int passed = results.Count(r => r.Passed);
        foreach (var r in results)
        {
            var status = r.Passed ? "PASS" : "FAIL";
            Console.WriteLine($"  [{status}] {r.Name}{(r.Failure is null ? "" : "  — " + r.Failure)}");
        }
        Console.WriteLine($"\n{passed}/{results.Count} passed.\n");
    }

    // ─── tests ───────────────────────────────────────────────────────────────

    private static Task<TestResult> Test_Strike_Deals6() =>
        SingleCardTest<StrikeIronclad>(
            name: "Strike deals 6",
            assert: (h, before) => Expect(before.dummyHp - h.Dummy.CurrentHp, 6, "damage"));

    private static Task<TestResult> Test_TwinStrike_DealsFiveTwice() =>
        SingleCardTest<TwinStrike>(
            name: "TwinStrike deals 5 twice (10)",
            assert: (h, before) => Expect(before.dummyHp - h.Dummy.CurrentHp, 10, "damage"));

    private static Task<TestResult> Test_PommelStrike_DealsNineAndDrawsOne() =>
        SingleCardTest<PommelStrike>(
            name: "PommelStrike deals 9 + draws 1",
            extraDeckCards: new[] { typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad) },
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 9) return $"expected 9 damage, got {dmg}";
                // Pommel was in hand, gets moved to Play pile when cast (-1).
                // Draw 1 from DrawPile -> Hand (+1). Net hand size: unchanged.
                // Reliable signal: DrawPile shrunk by exactly 1.
                var drawAfter = h.Player.PlayerCombatState!.DrawPile.Cards.Count;
                if (drawAfter != before.drawSize - 1)
                    return $"expected draw pile size {before.drawSize - 1} after draw, got {drawAfter}";
                return null;
            });

    private static Task<TestResult> Test_Bloodletting_HpDownEnergyUp() =>
        SingleCardTest<Bloodletting>(
            name: "Bloodletting: -3 HP, +2 energy",
            assert: (h, before) =>
            {
                var hpDelta = before.playerHp - h.Player.Creature.CurrentHp;
                if (hpDelta != 3) return $"expected -3 player HP, got {-hpDelta}";
                var pcs = h.Player.PlayerCombatState!;
                if (pcs.Energy != before.energy + 2)
                    return $"expected energy {before.energy + 2}, got {pcs.Energy}";
                return null;
            });

    private static Task<TestResult> Test_Defend_AddsBlock() =>
        SingleCardTest<DefendIronclad>(
            name: "Defend adds 5 block",
            assert: (h, before) => Expect(h.Player.Creature.Block - before.playerBlock, 5, "block"));

    private static Task<TestResult> Test_Bash_AppliesVulnerable() =>
        SingleCardTest<Bash>(
            name: "Bash deals 8 + applies Vulnerable",
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 8) return $"expected 8 damage, got {dmg}";
                if (!h.Dummy.Powers.Any(p => p.Id.Entry.Contains("VULNERABLE")))
                    return $"expected Vulnerable on dummy, got [{string.Join(",", h.Dummy.Powers.Select(p => p.Id.Entry))}]";
                return null;
            });

    private static async Task<TestResult> Test_Hellraiser_AutoplaysDrawnStrikes()
    {
        // After Hellraiser power is applied, drawing a Strike should auto-play it.
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new List<Type>
        {
            typeof(Hellraiser),
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
        });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 5);

            // Move Hellraiser into hand and play it.
            var hellraiser = pcs.DrawPile.Cards.OfType<Hellraiser>().First();
            pcs.DrawPile.RemoveInternal(hellraiser);
            pcs.Hand.AddInternal(hellraiser);
            await hellraiser.OnPlayWrapper(h.Ctx, h.Player.Creature, isAutoPlay: true,
                new ResourceInfo { EnergySpent = 2, EnergyValue = 2, StarsSpent = 0, StarValue = 0 },
                skipCardPileVisuals: true);

            // Now player has Hellraiser power. Draw 1 — should be a Strike, and
            // it should auto-play (dealing 6 dmg to dummy).
            var hpBefore = h.Dummy.CurrentHp;
            await MegaCrit.Sts2.Core.Commands.CardPileCmd.Draw(h.Ctx, 1, h.Player);
            var dmg = hpBefore - h.Dummy.CurrentHp;
            return new TestResult("Hellraiser autoplays drawn Strikes", dmg == 6, dmg == 6 ? null : $"expected 6 dmg from auto-played Strike, got {dmg}");
        }
        finally { Harness.EndCombat(); }
    }

    private static async Task<TestResult> Test_Offering_GivesEnergyAndDraw()
    {
        // Offering: lose 6 HP, gain 2 energy, draw 3, exhaust.
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new List<Type>
        {
            typeof(Offering),
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
        });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 0); // start with zero so we can verify Offering gave +2
            var offering = pcs.DrawPile.Cards.OfType<Offering>().First();
            pcs.DrawPile.RemoveInternal(offering);
            pcs.Hand.AddInternal(offering);

            var hpBefore = h.Player.Creature.CurrentHp;
            var handBefore = pcs.Hand.Cards.Count;

            await offering.OnPlayWrapper(h.Ctx, h.Player.Creature, isAutoPlay: true,
                new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 },
                skipCardPileVisuals: true);

            // Check: HP -6, energy = 2 (was 0 + gain 2), 3 cards drawn (-1 played offering)
            var hpDelta = hpBefore - h.Player.Creature.CurrentHp;
            if (hpDelta != 6) return new TestResult("Offering gives +2 energy and draws 3", false, $"HP delta expected 6, got {hpDelta}");
            if (pcs.Energy != 2) return new TestResult("Offering gives +2 energy and draws 3", false, $"Energy expected 2, got {pcs.Energy}");
            // hand was {Offering}; played -> Play pile or Exhaust; +3 from Draw → total 3
            if (pcs.Hand.Cards.Count != 3) return new TestResult("Offering gives +2 energy and draws 3", false, $"Hand expected 3, got {pcs.Hand.Cards.Count}");
            return new TestResult("Offering gives +2 energy and draws 3", true, null);
        }
        finally { Harness.EndCombat(); }
    }

    private static async Task<TestResult> Test_BashPlus_Deals10()
    {
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new[] { new Harness.DeckEntry(typeof(Bash), UpgradeLevel: 1) });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 3);
            var bash = pcs.DrawPile.Cards.OfType<Bash>().First();
            pcs.DrawPile.RemoveInternal(bash); pcs.Hand.AddInternal(bash);

            var hp = h.Dummy.CurrentHp;
            await bash.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true,
                new ResourceInfo { EnergySpent = 2, EnergyValue = 2, StarsSpent = 0, StarValue = 0 },
                skipCardPileVisuals: true);
            var dmg = hp - h.Dummy.CurrentHp;
            return new TestResult("Bash+ deals 10 (8+2 from upgrade)", dmg == 10, dmg == 10 ? null : $"got {dmg}");
        }
        finally { Harness.EndCombat(); }
    }

    private static async Task<TestResult> Test_StrikePlus_Deals9()
    {
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new[] { new Harness.DeckEntry(typeof(StrikeIronclad), UpgradeLevel: 1) });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 3);
            var strike = pcs.DrawPile.Cards.OfType<StrikeIronclad>().First();
            pcs.DrawPile.RemoveInternal(strike); pcs.Hand.AddInternal(strike);

            var hp = h.Dummy.CurrentHp;
            await strike.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true,
                new ResourceInfo { EnergySpent = 1, EnergyValue = 1, StarsSpent = 0, StarValue = 0 },
                skipCardPileVisuals: true);
            var dmg = hp - h.Dummy.CurrentHp;
            return new TestResult("Strike+ deals 9 (6+3 from upgrade)", dmg == 9, dmg == 9 ? null : $"got {dmg}");
        }
        finally { Harness.EndCombat(); }
    }

    private static async Task<TestResult> Test_Inflame_BoostsLaterStrike()
    {
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new List<Type> { typeof(Inflame), typeof(StrikeIronclad) });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 3);

            var inflame = pcs.DrawPile.Cards.OfType<Inflame>().First();
            var strike = pcs.DrawPile.Cards.OfType<StrikeIronclad>().First();
            pcs.DrawPile.RemoveInternal(inflame); pcs.Hand.AddInternal(inflame);
            pcs.DrawPile.RemoveInternal(strike); pcs.Hand.AddInternal(strike);

            var resources = new ResourceInfo { EnergySpent = 1, EnergyValue = 1, StarsSpent = 0, StarValue = 0 };

            int hp1 = h.Dummy.CurrentHp;
            await inflame.OnPlayWrapper(h.Ctx, h.Player.Creature, isAutoPlay: true, resources, skipCardPileVisuals: true);
            int inflameDmg = hp1 - h.Dummy.CurrentHp;

            int hp2 = h.Dummy.CurrentHp;
            await strike.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true, resources, skipCardPileVisuals: true);
            int strikeDmg = hp2 - h.Dummy.CurrentHp;

            if (inflameDmg != 0) return new TestResult("Inflame: 0 dmg + Strength → Strike does 8 (6+2)", false, $"Inflame dmg expected 0, got {inflameDmg}");
            if (strikeDmg != 8) return new TestResult("Inflame: 0 dmg + Strength → Strike does 8 (6+2)", false, $"Strike-after-Inflame expected 8, got {strikeDmg}");
            return new TestResult("Inflame: 0 dmg + Strength → Strike does 8 (6+2)", true, null);
        }
        finally { Harness.EndCombat(); }
    }

    private static async Task<TestResult> Test_StrikeIntoVulnerable_Deals9()
    {
        var h = Harness.BeginCombat<Ironclad>(deckOverride: new List<Type> { typeof(Bash), typeof(StrikeIronclad) });
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            SetEnergy(pcs, 3);

            var bash = pcs.DrawPile.Cards.OfType<Bash>().First();
            var strike = pcs.DrawPile.Cards.OfType<StrikeIronclad>().First();
            pcs.DrawPile.RemoveInternal(bash); pcs.Hand.AddInternal(bash);
            pcs.DrawPile.RemoveInternal(strike); pcs.Hand.AddInternal(strike);

            var resources = new ResourceInfo { EnergySpent = 0, EnergyValue = 0, StarsSpent = 0, StarValue = 0 };

            int hp1 = h.Dummy.CurrentHp;
            await bash.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true, resources, skipCardPileVisuals: true);
            int bashDmg = hp1 - h.Dummy.CurrentHp;

            int hp2 = h.Dummy.CurrentHp;
            await strike.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true, resources, skipCardPileVisuals: true);
            int strikeDmg = hp2 - h.Dummy.CurrentHp;

            if (bashDmg != 8) return new TestResult("Strike into Vulnerable does 9 (6×1.5)", false, $"bash dmg expected 8, got {bashDmg}");
            if (strikeDmg != 9) return new TestResult("Strike into Vulnerable does 9 (6×1.5)", false, $"strike-after-vulnerable dmg expected 9, got {strikeDmg}");
            return new TestResult("Strike into Vulnerable does 9 (6×1.5)", true, null);
        }
        finally { Harness.EndCombat(); }
    }

    private static Task<TestResult> Test_Feed_ExhaustsAfterPlay() =>
        SingleCardTest<Feed>(
            name: "Feed lands in exhaust pile after play",
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                if (pcs.ExhaustPile.Cards.Count != 1)
                    return $"expected 1 card in exhaust pile, got {pcs.ExhaustPile.Cards.Count}";
                if (!pcs.ExhaustPile.Cards.Any(c => c is Feed))
                    return "exhaust pile does not contain a Feed";
                return null;
            });

    // ─── helpers ────────────────────────────────────────────────────────────

    private record SnapshotBefore(int dummyHp, int playerHp, int playerBlock, int energy, int handSize, int drawSize);

    private static async Task<TestResult> SingleCardTest<TCard>(
        string name,
        Func<Harness.CombatHarness, SnapshotBefore, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null)
        where TCard : CardModel
    {
        var deck = new List<Type> { typeof(TCard) };
        if (extraDeckCards != null) deck.AddRange(extraDeckCards);

        var h = Harness.BeginCombat<Ironclad>(deckOverride: deck);
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            // Set energy explicitly.
            SetEnergy(pcs, 3);

            // Move the card-under-test from draw pile to hand.
            var card = pcs.DrawPile.Cards.OfType<TCard>().First();
            pcs.DrawPile.RemoveInternal(card);
            pcs.Hand.AddInternal(card);

            var before = new SnapshotBefore(
                dummyHp: h.Dummy.CurrentHp,
                playerHp: h.Player.Creature.CurrentHp,
                playerBlock: h.Player.Creature.Block,
                energy: pcs.Energy,
                handSize: pcs.Hand.Cards.Count,
                drawSize: pcs.DrawPile.Cards.Count);

            var resources = new ResourceInfo
            {
                EnergySpent = card.EnergyCost.GetResolved(),
                EnergyValue = card.EnergyCost.GetResolved(),
                StarsSpent = 0,
                StarValue = 0,
            };
            await card.OnPlayWrapper(h.Ctx, h.Dummy, isAutoPlay: true, resources, skipCardPileVisuals: true);

            var failure = assert(h, before);
            return new TestResult(name, failure is null, failure);
        }
        finally
        {
            Harness.EndCombat();
        }
    }

    private static string? Expect<T>(T actual, T expected, string what) where T : IEquatable<T>
        => actual.Equals(expected) ? null : $"expected {what}={expected}, got {actual}";

    private static void SetEnergy(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState pcs, int amount)
    {
        var prop = typeof(MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState)
            .GetProperty("Energy", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        prop!.SetValue(pcs, amount);
    }
}
