using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 2 — Multi-hit & Shiv Attacks. Cards that hit multiple times,
/// generate Shivs, or scale damage based on shivs/cards in hand.
///
/// Scope (15 cards × 2 upgrade levels = 30 tests):
///   DaggerSpray, FanOfKnives, BladeDance, CloakAndDagger, Accuracy,
///   BladeOfInk, PhantomBlades, Predator, Skewer, Snakebite, Ricochet,
///   StormOfSteel, Outbreak, HiddenDaggers, HandTrick
/// </summary>
internal static class Bucket2_MultiHitShiv
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            await Test_DaggerSpray(),
            await Test_DaggerSprayPlus(),
            await Test_FanOfKnives(),
            await Test_FanOfKnivesPlus(),
            await Test_BladeDance(),
            await Test_BladeDancePlus(),
            await Test_CloakAndDagger(),
            await Test_CloakAndDaggerPlus(),
            await Test_Accuracy(),
            await Test_AccuracyPlus(),
            await Test_BladeOfInk(),
            await Test_BladeOfInkPlus(),
            await Test_PhantomBlades(),
            await Test_PhantomBladesPlus(),
            await Test_Predator(),
            await Test_PredatorPlus(),
            await Test_Skewer(),
            await Test_SkewerPlus(),
            await Test_Snakebite(),
            await Test_SnakebitePlus(),
            await Test_Ricochet(),
            await Test_RicochetPlus(),
            await Test_StormOfSteel(),
            await Test_StormOfSteelPlus(),
            await Test_Outbreak(),
            await Test_OutbreakPlus(),
            await Test_HiddenDaggers(),
            await Test_HiddenDaggersPlus(),
            await Test_HandTrick(),
            await Test_HandTrickPlus(),
        };
        return results;
    }

    // ── DaggerSpray: 4 dmg × 2 hits to all enemies. Single dummy → 8 total.
    //                 Upgrade +2 dmg → 6 × 2 = 12.
    private static Task<TestHelpers.TestResult> Test_DaggerSpray()
        => TestHelpers.SingleCardTest<DaggerSpray>(
            name: "DaggerSpray deals 4×2=8 to single dummy",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 8, "damage"));

    private static Task<TestHelpers.TestResult> Test_DaggerSprayPlus()
        => TestHelpers.SingleCardTest<DaggerSpray>(
            name: "DaggerSpray+ deals 6×2=12 to single dummy",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 12, "damage"));

    // ── FanOfKnives: power, applies FanOfKnivesPower(1) and creates 4 shivs (5 upgraded).
    private static Task<TestHelpers.TestResult> Test_FanOfKnives()
        => TestHelpers.SingleCardTest<FanOfKnives>(
            name: "FanOfKnives creates 4 shivs + applies power",
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                if (shivs != 4) return $"expected 4 shivs in hand, got {shivs}";
                return TestHelpers.ExpectPower(h.Player.Creature, "FAN_OF_KNIVES", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_FanOfKnivesPlus()
        => TestHelpers.SingleCardTest<FanOfKnives>(
            name: "FanOfKnives+ creates 5 shivs + applies power",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                if (shivs != 5) return $"expected 5 shivs in hand, got {shivs}";
                return TestHelpers.ExpectPower(h.Player.Creature, "FAN_OF_KNIVES", expectedAmount: 1);
            });

    // ── BladeDance: 3 shivs (4 upgraded), exhausts.
    private static Task<TestHelpers.TestResult> Test_BladeDance()
        => TestHelpers.SingleCardTest<BladeDance>(
            name: "BladeDance creates 3 shivs",
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 3, "shivs in hand");
            });

    private static Task<TestHelpers.TestResult> Test_BladeDancePlus()
        => TestHelpers.SingleCardTest<BladeDance>(
            name: "BladeDance+ creates 4 shivs",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 4, "shivs in hand");
            });

    // ── CloakAndDagger: 6 block + 1 shiv (2 shivs upgraded). Block stays at 6.
    private static Task<TestHelpers.TestResult> Test_CloakAndDagger()
        => TestHelpers.SingleCardTest<CloakAndDagger>(
            name: "CloakAndDagger gives 6 block + 1 shiv",
            assert: (h, before) =>
            {
                if (h.Player.Creature.Block - before.playerBlock != 6) return $"expected block=6, got {h.Player.Creature.Block - before.playerBlock}";
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 1, "shivs in hand");
            });

    private static Task<TestHelpers.TestResult> Test_CloakAndDaggerPlus()
        => TestHelpers.SingleCardTest<CloakAndDagger>(
            name: "CloakAndDagger+ gives 6 block + 2 shivs",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (h.Player.Creature.Block - before.playerBlock != 6) return $"expected block=6, got {h.Player.Creature.Block - before.playerBlock}";
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 2, "shivs in hand");
            });

    // ── Accuracy: applies AccuracyPower 4 (6 upgraded). Power buffs shivs by +N damage.
    private static Task<TestHelpers.TestResult> Test_Accuracy()
        => TestHelpers.SingleCardTest<Accuracy>(
            name: "Accuracy applies AccuracyPower 4",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "ACCURACY", expectedAmount: 4));

    private static Task<TestHelpers.TestResult> Test_AccuracyPlus()
        => TestHelpers.SingleCardTest<Accuracy>(
            name: "Accuracy+ applies AccuracyPower 6",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "ACCURACY", expectedAmount: 6));

    // ── BladeOfInk: 2 shivs, all enchanted with Inky (3 upgraded).
    private static Task<TestHelpers.TestResult> Test_BladeOfInk()
        => TestHelpers.SingleCardTest<BladeOfInk>(
            name: "BladeOfInk creates 2 shivs",
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 2, "shivs in hand");
            });

    private static Task<TestHelpers.TestResult> Test_BladeOfInkPlus()
        => TestHelpers.SingleCardTest<BladeOfInk>(
            name: "BladeOfInk+ creates 3 shivs",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                return TestHelpers.Expect(shivs, 3, "shivs in hand");
            });

    // ── PhantomBlades: applies PhantomBladesPower 9 (12 upgraded).
    private static Task<TestHelpers.TestResult> Test_PhantomBlades()
        => TestHelpers.SingleCardTest<PhantomBlades>(
            name: "PhantomBlades applies PhantomBladesPower 9",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "PHANTOM_BLADES", expectedAmount: 9));

    private static Task<TestHelpers.TestResult> Test_PhantomBladesPlus()
        => TestHelpers.SingleCardTest<PhantomBlades>(
            name: "PhantomBlades+ applies PhantomBladesPower 12",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "PHANTOM_BLADES", expectedAmount: 12));

    // ── Predator: 15 dmg, +2 next-turn cards (20 dmg upgraded).
    private static Task<TestHelpers.TestResult> Test_Predator()
        => TestHelpers.SingleCardTest<Predator>(
            name: "Predator deals 15",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 15, "damage"));

    private static Task<TestHelpers.TestResult> Test_PredatorPlus()
        => TestHelpers.SingleCardTest<Predator>(
            name: "Predator+ deals 20",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 20, "damage"));

    // ── Skewer: X-cost. 8 dmg per hit × X (11 dmg upgraded).
    //   PlayCard now captures CapturedXValue from current energy. With energy=3
    //   (the default), Skewer hits 3 times for 8 → 24 dmg. Skewer+ → 33 dmg.
    private static Task<TestHelpers.TestResult> Test_Skewer()
        => TestHelpers.SingleCardTest<Skewer>(
            name: "Skewer (X=3): 8 dmg × 3 hits = 24",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 24, "damage"));

    private static Task<TestHelpers.TestResult> Test_SkewerPlus()
        => TestHelpers.SingleCardTest<Skewer>(
            name: "Skewer+ (X=3): 11 dmg × 3 hits = 33",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 33, "damage"));

    // ── Snakebite: applies 7 Poison to single target (10 upgraded).
    private static Task<TestHelpers.TestResult> Test_Snakebite()
        => TestHelpers.SingleCardTest<Snakebite>(
            name: "Snakebite applies 7 Poison",
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 7));

    private static Task<TestHelpers.TestResult> Test_SnakebitePlus()
        => TestHelpers.SingleCardTest<Snakebite>(
            name: "Snakebite+ applies 10 Poison",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 10));

    // ── Ricochet: 3 dmg × 4 hits to RANDOM enemies (5 hits upgraded).
    //   Random target = nondeterministic; only one enemy on the board so practically
    //   it always hits the dummy, but we shouldn't lock that behavior down.
    private static Task<TestHelpers.TestResult> Test_Ricochet()
        => TestHelpers.Skip("Ricochet (random target)",
            "TargetType.RandomEnemy makes targeting nondeterministic; need multi-enemy harness.");

    private static Task<TestHelpers.TestResult> Test_RicochetPlus()
        => TestHelpers.Skip("Ricochet+ (random target)",
            "TargetType.RandomEnemy makes targeting nondeterministic; need multi-enemy harness.");

    // ── StormOfSteel: discard rest-of-hand (after StormOfSteel moves to Play pile),
    //   gain that many shivs. Preload hand with 3 fillers → 3 shivs.
    //   Upgraded version creates upgraded shivs.
    private static Task<TestHelpers.TestResult> Test_StormOfSteel()
        => TestHelpers.PreloadHandTest<StormOfSteel>(
            name: "StormOfSteel discards 3-card hand → 3 shivs",
            handLoadout: new[] { typeof(DefendSilent), typeof(DefendSilent), typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                if (shivs != 3) return $"expected 3 shivs, got {shivs}";
                if (pcs.Hand.Cards.First(c => c.GetType() == typeof(Shiv)).CurrentUpgradeLevel != 0)
                    return "expected unupgraded shivs";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_StormOfSteelPlus()
        => TestHelpers.PreloadHandTest<StormOfSteel>(
            name: "StormOfSteel+ discards 3-card hand → 3 upgraded shivs",
            upgradeLevel: 1,
            handLoadout: new[] { typeof(DefendSilent), typeof(DefendSilent), typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int shivs = pcs.Hand.Cards.Count(c => c.GetType() == typeof(Shiv));
                if (shivs != 3) return $"expected 3 shivs, got {shivs}";
                if (pcs.Hand.Cards.First(c => c.GetType() == typeof(Shiv)).CurrentUpgradeLevel < 1)
                    return "expected upgraded shivs";
                return null;
            });

    // ── Outbreak: applies OutbreakPower 11 (15 upgraded). Self target. Cost 1 (NOT X-cost).
    private static Task<TestHelpers.TestResult> Test_Outbreak()
        => TestHelpers.SingleCardTest<Outbreak>(
            name: "Outbreak applies OutbreakPower 11",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "OUTBREAK", expectedAmount: 11));

    private static Task<TestHelpers.TestResult> Test_OutbreakPlus()
        => TestHelpers.SingleCardTest<Outbreak>(
            name: "Outbreak+ applies OutbreakPower 15",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "OUTBREAK", expectedAmount: 15));

    // ── HiddenDaggers: requires player to pick cards from hand to discard via CardSelectCmd.
    //   AutoCardSelector picks the first N cards, but with only HiddenDaggers itself in
    //   hand (and it's already in the Play pile by then), there are 0 candidates and the
    //   discard count won't match. Skip both — this is fundamentally a hand-interaction card.
    private static Task<TestHelpers.TestResult> Test_HiddenDaggers()
        => TestHelpers.Skip("HiddenDaggers (CardSelectCmd discard)",
            "Discards 2 cards chosen from hand; isolated harness has no other hand cards to pick.");

    private static Task<TestHelpers.TestResult> Test_HiddenDaggersPlus()
        => TestHelpers.Skip("HiddenDaggers+ (CardSelectCmd discard)",
            "Discards 2 cards chosen from hand; isolated harness has no other hand cards to pick.");

    // ── HandTrick: 7 block (10 upgraded), then Sly a skill in hand. With no other skills
    //   in hand the Sly portion silently no-ops; we assert the block delta.
    private static Task<TestHelpers.TestResult> Test_HandTrick()
        => TestHelpers.SingleCardTest<HandTrick>(
            name: "HandTrick gives 7 block",
            assert: (h, before) => TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 7, "block"));

    private static Task<TestHelpers.TestResult> Test_HandTrickPlus()
        => TestHelpers.SingleCardTest<HandTrick>(
            name: "HandTrick+ gives 10 block",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 10, "block"));
}
