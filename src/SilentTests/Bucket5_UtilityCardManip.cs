using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 5 — Utility & Card Manipulation. Discard, search, choose-from-hand,
/// X-cost. Highest crash-rate bucket — these touch the most game APIs.
///
/// Scope (16 cards × 2 upgrade levels = 32 tests):
///   Adrenaline, Expertise, ToolsOfTheTrade, CalculatedGamble, MasterPlanner,
///   WellLaidPlans, Nightmare, Pinpoint, UpMySleeve, Abrasive, Accelerant,
///   Mirage, Haze, Speedster, Shadowmeld, TheHunt
/// </summary>
internal static class Bucket5_UtilityCardManip
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>();

        results.Add(await Test_Adrenaline());
        results.Add(await Test_AdrenalinePlus());
        results.Add(await Test_Expertise());
        results.Add(await Test_ExpertisePlus());
        results.Add(await Test_ToolsOfTheTrade());
        results.Add(await Test_ToolsOfTheTradePlus());
        results.Add(await Test_CalculatedGamble());
        results.Add(await Test_CalculatedGamblePlus());
        results.Add(await Test_MasterPlanner());
        results.Add(await Test_MasterPlannerPlus());
        results.Add(await Test_WellLaidPlans());
        results.Add(await Test_WellLaidPlansPlus());
        results.Add(await Test_Nightmare());
        results.Add(await Test_NightmarePlus());
        results.Add(await Test_Pinpoint());
        results.Add(await Test_PinpointPlus());
        results.Add(await Test_UpMySleeve());
        results.Add(await Test_UpMySleevePlus());
        results.Add(await Test_Abrasive());
        results.Add(await Test_AbrasivePlus());
        results.Add(await Test_Accelerant());
        results.Add(await Test_AccelerantPlus());
        results.Add(await Test_Mirage());
        results.Add(await Test_MiragePlus());
        results.Add(await Test_Haze());
        results.Add(await Test_HazePlus());
        results.Add(await Test_Speedster());
        results.Add(await Test_SpeedsterPlus());
        results.Add(await Test_Shadowmeld());
        results.Add(await Test_ShadowmeldPlus());
        results.Add(await Test_TheHunt());
        results.Add(await Test_TheHuntPlus());

        return results;
    }

    // ─── Adrenaline ──────────────────────────────────────────────────────────
    // Base: gain 1 Energy, draw 2. Upgrade: gain 2 Energy, draw 2. Exhaust.

    private static Task<TestHelpers.TestResult> Test_Adrenaline()
        => TestHelpers.SingleCardTest<Adrenaline>(
            name: "Adrenaline gains 1 energy, draws 2",
            energy: 3,
            extraDeckCards: new[] { typeof(DefendSilent), typeof(DefendSilent), typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                var energyDelta = pcs.Energy - before.energy;
                if (energyDelta != 1) return $"expected +1 energy, got {energyDelta}";
                // Hand started with [Adrenaline] (1 card). Card removed by play (-> 0),
                // then draw 2 from draw pile. Final hand = 2. Delta from before = +1.
                var handDelta = pcs.Hand.Cards.Count - before.handSize;
                if (handDelta != 1) return $"expected hand delta=+1, got {handDelta}";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_AdrenalinePlus()
        => TestHelpers.SingleCardTest<Adrenaline>(
            name: "Adrenaline+ gains 2 energy, draws 2",
            upgradeLevel: 1,
            energy: 3,
            extraDeckCards: new[] { typeof(DefendSilent), typeof(DefendSilent), typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                var energyDelta = pcs.Energy - before.energy;
                if (energyDelta != 2) return $"expected +2 energy, got {energyDelta}";
                var handDelta = pcs.Hand.Cards.Count - before.handSize;
                if (handDelta != 1) return $"expected hand delta=+1, got {handDelta}";
                return null;
            });

    // ─── Expertise ───────────────────────────────────────────────────────────
    // Draw cards until you have 6 in hand (7 upgraded).

    private static Task<TestHelpers.TestResult> Test_Expertise()
        => TestHelpers.SingleCardTest<Expertise>(
            name: "Expertise draws until 6 in hand",
            extraDeckCards: Enumerable.Repeat(typeof(DefendSilent), 8).ToArray(),
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                if (pcs.Hand.Cards.Count != 6)
                    return $"expected hand count=6, got {pcs.Hand.Cards.Count}";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_ExpertisePlus()
        => TestHelpers.SingleCardTest<Expertise>(
            name: "Expertise+ draws until 7 in hand",
            upgradeLevel: 1,
            extraDeckCards: Enumerable.Repeat(typeof(DefendSilent), 9).ToArray(),
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                if (pcs.Hand.Cards.Count != 7)
                    return $"expected hand count=7, got {pcs.Hand.Cards.Count}";
                return null;
            });

    // ─── ToolsOfTheTrade ─────────────────────────────────────────────────────
    // Power. Applies ToolsOfTheTradePower 1. Upgrade reduces cost by 1.

    private static Task<TestHelpers.TestResult> Test_ToolsOfTheTrade()
        => TestHelpers.SingleCardTest<ToolsOfTheTrade>(
            name: "ToolsOfTheTrade applies power",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "TOOLS"));

    private static Task<TestHelpers.TestResult> Test_ToolsOfTheTradePlus()
        => TestHelpers.SingleCardTest<ToolsOfTheTrade>(
            name: "ToolsOfTheTrade+ applies power (cost reduced)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "TOOLS"));

    // ─── CalculatedGamble ────────────────────────────────────────────────────
    // Discard your hand, draw same number. Exhaust. Upgrade adds Retain.

    // CalculatedGamble: discard hand, draw same number. Test by putting Defends in
    // hand and Strikes in draw pile — after play, hand should be Strikes (drawn from
    // pile), and discard should hold the Defends. This makes the swap observable
    // (a same-card test is invisible because cards reshuffle back in).
    private static async Task<TestHelpers.TestResult> Test_CalculatedGamble()
    {
        var h = Harness.BeginCombat<MegaCrit.Sts2.Core.Models.Characters.Silent>(new List<Harness.DeckEntry>
        {
            new(typeof(CalculatedGamble)),
            new(typeof(DefendSilent)), new(typeof(DefendSilent)),
            new(typeof(DefendSilent)), new(typeof(DefendSilent)),
            new(typeof(StrikeSilent)), new(typeof(StrikeSilent)),
            new(typeof(StrikeSilent)), new(typeof(StrikeSilent)),
        }, shuffleSeed: 1);
        try
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, 3);
            // Hand = [CalcGamble + 4 Defends]. Draw pile = [4 Strikes].
            var calc = pcs.DrawPile.Cards.OfType<CalculatedGamble>().First();
            pcs.DrawPile.RemoveInternal(calc); pcs.Hand.AddInternal(calc);
            foreach (var c in pcs.DrawPile.Cards.OfType<DefendSilent>().ToList())
            { pcs.DrawPile.RemoveInternal(c); pcs.Hand.AddInternal(c); }

            await TestHelpers.PlayCard(h, calc);

            // Expect: 4 Defends in discard, 4 Strikes in hand, CalcGamble exhausted.
            var defendsDiscarded = pcs.DiscardPile.Cards.Count(c => c is DefendSilent);
            var strikesInHand = pcs.Hand.Cards.Count(c => c is StrikeSilent);
            if (defendsDiscarded != 4)
                return new TestHelpers.TestResult("CalculatedGamble discards 4 Defends, draws 4 Strikes",
                    TestHelpers.Outcome.Fail, $"expected 4 Defends in discard, got {defendsDiscarded}");
            if (strikesInHand != 4)
                return new TestHelpers.TestResult("CalculatedGamble discards 4 Defends, draws 4 Strikes",
                    TestHelpers.Outcome.Fail, $"expected 4 Strikes in hand, got {strikesInHand}");
            return new TestHelpers.TestResult("CalculatedGamble discards 4 Defends, draws 4 Strikes",
                TestHelpers.Outcome.Pass);
        }
        finally { Harness.EndCombat(); }
    }

    // CalculatedGamble+: same effect, plus the Retain keyword. Verify by checking
    // the keyword is present on a freshly upgraded card.
    private static async Task<TestHelpers.TestResult> Test_CalculatedGamblePlus()
    {
        var h = Harness.BeginCombat<MegaCrit.Sts2.Core.Models.Characters.Silent>(new List<Harness.DeckEntry>
        {
            new(typeof(CalculatedGamble), 1),
        }, shuffleSeed: 1);
        try
        {
            var calc = h.Player.PlayerCombatState!.DrawPile.Cards.OfType<CalculatedGamble>().First();
            return calc.Keywords.Contains(MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.Retain)
                ? new TestHelpers.TestResult("CalculatedGamble+ has Retain keyword", TestHelpers.Outcome.Pass)
                : new TestHelpers.TestResult("CalculatedGamble+ has Retain keyword", TestHelpers.Outcome.Fail,
                    $"expected Retain keyword, got [{string.Join(",", calc.Keywords)}]");
        }
        finally { Harness.EndCombat(); }
    }

    // ─── MasterPlanner ───────────────────────────────────────────────────────

    private static Task<TestHelpers.TestResult> Test_MasterPlanner()
        => TestHelpers.SingleCardTest<MasterPlanner>(
            name: "MasterPlanner applies power",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "PLANNER"));

    private static Task<TestHelpers.TestResult> Test_MasterPlannerPlus()
        => TestHelpers.SingleCardTest<MasterPlanner>(
            name: "MasterPlanner+ applies power (cost reduced)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "PLANNER"));

    // ─── WellLaidPlans ───────────────────────────────────────────────────────
    // Power. Applies WellLaidPlansPower with RetainAmount 1 (or 2 upgraded).

    private static Task<TestHelpers.TestResult> Test_WellLaidPlans()
        => TestHelpers.SingleCardTest<WellLaidPlans>(
            name: "WellLaidPlans applies power (1)",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "PLANS", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_WellLaidPlansPlus()
        => TestHelpers.SingleCardTest<WellLaidPlans>(
            name: "WellLaidPlans+ applies power (2)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "PLANS", expectedAmount: 2));

    // ─── Nightmare ───────────────────────────────────────────────────────────
    // Choose a card from hand, apply NightmarePower with that card. Depends on
    // AutoCardSelector.FromHand behavior — may crash.

    private static Task<TestHelpers.TestResult> Test_Nightmare()
        => TestHelpers.PreloadHandTest<Nightmare>(
            name: "Nightmare picks card and applies power",
            handLoadout: new[] { typeof(DefendSilent) },
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "NIGHTMARE"));

    private static Task<TestHelpers.TestResult> Test_NightmarePlus()
        => TestHelpers.PreloadHandTest<Nightmare>(
            name: "Nightmare+ picks card and applies power (cost reduced)",
            upgradeLevel: 1,
            handLoadout: new[] { typeof(DefendSilent) },
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "NIGHTMARE"));

    // ─── Pinpoint ────────────────────────────────────────────────────────────
    // 15 dmg / 19 dmg upgraded.

    private static Task<TestHelpers.TestResult> Test_Pinpoint()
        => TestHelpers.SingleCardTest<Pinpoint>(
            name: "Pinpoint deals 15",
            assert: (h, before) =>
                TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 15, "damage"));

    private static Task<TestHelpers.TestResult> Test_PinpointPlus()
        => TestHelpers.SingleCardTest<Pinpoint>(
            name: "Pinpoint+ deals 19",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 19, "damage"));

    // ─── UpMySleeve ──────────────────────────────────────────────────────────
    // Adds 3 (or 4 upgraded) Shivs to hand.

    private static Task<TestHelpers.TestResult> Test_UpMySleeve()
        => TestHelpers.SingleCardTest<UpMySleeve>(
            name: "UpMySleeve adds 3 Shivs to hand",
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                var shivCount = pcs.Hand.Cards.OfType<Shiv>().Count();
                return TestHelpers.Expect(shivCount, 3, "shivs in hand");
            });

    private static Task<TestHelpers.TestResult> Test_UpMySleevePlus()
        => TestHelpers.SingleCardTest<UpMySleeve>(
            name: "UpMySleeve+ adds 4 Shivs to hand",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                var shivCount = pcs.Hand.Cards.OfType<Shiv>().Count();
                return TestHelpers.Expect(shivCount, 4, "shivs in hand");
            });

    // ─── Abrasive ────────────────────────────────────────────────────────────
    // Power: Dexterity 1, Thorns 4 (or 6 upgraded).

    private static Task<TestHelpers.TestResult> Test_Abrasive()
        => TestHelpers.SingleCardTest<Abrasive>(
            name: "Abrasive applies Dex 1 + Thorns 4",
            assert: (h, before) =>
            {
                var dexErr = TestHelpers.ExpectPower(h.Player.Creature, "DEXTERITY", expectedAmount: 1);
                if (dexErr != null) return dexErr;
                return TestHelpers.ExpectPower(h.Player.Creature, "THORNS", expectedAmount: 4);
            });

    private static Task<TestHelpers.TestResult> Test_AbrasivePlus()
        => TestHelpers.SingleCardTest<Abrasive>(
            name: "Abrasive+ applies Dex 1 + Thorns 6",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var dexErr = TestHelpers.ExpectPower(h.Player.Creature, "DEXTERITY", expectedAmount: 1);
                if (dexErr != null) return dexErr;
                return TestHelpers.ExpectPower(h.Player.Creature, "THORNS", expectedAmount: 6);
            });

    // ─── Accelerant ──────────────────────────────────────────────────────────

    private static Task<TestHelpers.TestResult> Test_Accelerant()
        => TestHelpers.SingleCardTest<Accelerant>(
            name: "Accelerant applies power (1)",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "ACCELERANT", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_AccelerantPlus()
        => TestHelpers.SingleCardTest<Accelerant>(
            name: "Accelerant+ applies power (2)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "ACCELERANT", expectedAmount: 2));

    // ─── Mirage ──────────────────────────────────────────────────────────────
    // Block scales with sum of Poison stacks on enemies. Multiplier = sum.
    // (CalcBase=0 + CalcExtra=1) * sum_poison. With 5 Poison: block = 5.

    private static Task<TestHelpers.TestResult> Test_Mirage()
        => TestHelpers.PowerThenPlayTest<PoisonPower, Mirage>(
            name: "Mirage gains block from enemy Poison (5 → 5 block)",
            powerAmount: 5m,
            powerOnEnemy: true,
            assert: (h, before) =>
                TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 5, "block"));

    private static Task<TestHelpers.TestResult> Test_MiragePlus()
        => TestHelpers.PowerThenPlayTest<PoisonPower, Mirage>(
            name: "Mirage+ gains block from enemy Poison (cost reduced)",
            upgradeLevel: 1,
            powerAmount: 5m,
            powerOnEnemy: true,
            assert: (h, before) =>
                TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 5, "block"));

    // ─── Haze ────────────────────────────────────────────────────────────────
    // Apply Poison 4 (or 6 upgraded) to all enemies.

    private static Task<TestHelpers.TestResult> Test_Haze()
        => TestHelpers.SingleCardTest<Haze>(
            name: "Haze applies Poison 4 to enemy",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 4));

    private static Task<TestHelpers.TestResult> Test_HazePlus()
        => TestHelpers.SingleCardTest<Haze>(
            name: "Haze+ applies Poison 6 to enemy",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 6));

    // ─── Speedster ───────────────────────────────────────────────────────────
    // Power: SpeedsterPower 2. Upgrade adds Innate.

    private static Task<TestHelpers.TestResult> Test_Speedster()
        => TestHelpers.SingleCardTest<Speedster>(
            name: "Speedster applies power (2)",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "SPEEDSTER", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_SpeedsterPlus()
        => TestHelpers.SingleCardTest<Speedster>(
            name: "Speedster+ applies power (2, Innate)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "SPEEDSTER", expectedAmount: 2));

    // ─── Shadowmeld ──────────────────────────────────────────────────────────

    private static Task<TestHelpers.TestResult> Test_Shadowmeld()
        => TestHelpers.SingleCardTest<Shadowmeld>(
            name: "Shadowmeld applies power (1)",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "SHADOWMELD", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_ShadowmeldPlus()
        => TestHelpers.SingleCardTest<Shadowmeld>(
            name: "Shadowmeld+ applies power (1, cost reduced)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "SHADOWMELD", expectedAmount: 1));

    // ─── TheHunt ─────────────────────────────────────────────────────────────
    // 10 dmg / 15 dmg. Entire OnPlay body is gated on `currentRoom is CombatRoom`.
    // Our harness uses NullRunState (CurrentRoom=null), so the card no-ops.
    // Skipping rather than asserting 0 damage — that would silently pass even
    // if the harness later starts feeding a real CombatRoom.

    private static Task<TestHelpers.TestResult> Test_TheHunt()
        => TestHelpers.Skip("TheHunt deals 10 (gated on CombatRoom)",
            "OnPlay is wrapped in `if (currentRoom is CombatRoom)`. NullRunState " +
            "returns CurrentRoom=null, so the entire attack block is skipped. Needs " +
            "a real CombatRoom on the run state to test.");

    private static Task<TestHelpers.TestResult> Test_TheHuntPlus()
        => TestHelpers.Skip("TheHunt+ deals 15 (gated on CombatRoom)",
            "Same NullRunState gate as base TheHunt — entire OnPlay body is skipped " +
            "when CurrentRoom is null.");
}
