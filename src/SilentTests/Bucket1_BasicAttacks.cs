using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 1 — Basic Attacks. Pure damage with at most one simple side effect
/// (Weak/Vulnerable applied, draw 1, etc.). All cards in this bucket should
/// fit the SingleCardTest&lt;T&gt; pattern.
///
/// Scope (15 cards × 2 upgrade levels = 30 tests):
///   StrikeSilent, Sneaky, Slice, Pounce, FlickFlack, LeadingStrike,
///   EchoingSlash, PreciseCut, Murder, Flanking, FollowThrough, Backstab,
///   MementoMori, Neutralize, DaggerThrow
/// </summary>
internal static class Bucket1_BasicAttacks
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            await Test_StrikeSilent(),
            await Test_StrikeSilentPlus(),
            await Test_Sneaky(),
            await Test_SneakyPlus(),
            await Test_Slice(),
            await Test_SlicePlus(),
            await Test_Pounce(),
            await Test_PouncePlus(),
            await Test_FlickFlack(),
            await Test_FlickFlackPlus(),
            await Test_LeadingStrike(),
            await Test_LeadingStrikePlus(),
            await Test_EchoingSlash(),
            await Test_EchoingSlashPlus(),
            await Test_PreciseCut(),
            await Test_PreciseCutPlus(),
            await Test_Murder(),
            await Test_MurderPlus(),
            await Test_Flanking(),
            await Test_FlankingPlus(),
            await Test_FollowThrough(),
            await Test_FollowThroughPlus(),
            await Test_Backstab(),
            await Test_BackstabPlus(),
            await Test_MementoMori(),
            await Test_MementoMoriPlus(),
            await Test_Neutralize(),
            await Test_NeutralizePlus(),
            await Test_DaggerThrow(),
            await Test_DaggerThrowPlus(),
        };
        return results;
    }

    // ─── StrikeSilent: 6 dmg / 9 dmg ───────────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_StrikeSilent()
        => TestHelpers.SingleCardTest<StrikeSilent>(
            name: "StrikeSilent deals 6",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 6, "damage"));

    private static Task<TestHelpers.TestResult> Test_StrikeSilentPlus()
        => TestHelpers.SingleCardTest<StrikeSilent>(
            name: "StrikeSilent+ deals 9",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));

    // ─── Sneaky: applies SneakyPower 1 / 2 (multiplayer-only Power) ───────
    private static Task<TestHelpers.TestResult> Test_Sneaky()
        => TestHelpers.SingleCardTest<Sneaky>(
            name: "Sneaky applies SneakyPower 1",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "SNEAKY", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_SneakyPlus()
        => TestHelpers.SingleCardTest<Sneaky>(
            name: "Sneaky+ applies SneakyPower 2",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "SNEAKY", expectedAmount: 2));

    // ─── Slice: 6 dmg / 9 dmg (0-cost) ────────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Slice()
        => TestHelpers.SingleCardTest<Slice>(
            name: "Slice deals 6",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 6, "damage"));

    private static Task<TestHelpers.TestResult> Test_SlicePlus()
        => TestHelpers.SingleCardTest<Slice>(
            name: "Slice+ deals 9",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));

    // ─── Pounce: 12 dmg + FreeSkill 1 / 18 dmg + FreeSkill 1 ──────────────
    private static Task<TestHelpers.TestResult> Test_Pounce()
        => TestHelpers.SingleCardTest<Pounce>(
            name: "Pounce deals 12 + FreeSkill 1",
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 12) return $"expected damage=12, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Player.Creature, "FREE_SKILL", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_PouncePlus()
        => TestHelpers.SingleCardTest<Pounce>(
            name: "Pounce+ deals 18 + FreeSkill 1",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 18) return $"expected damage=18, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Player.Creature, "FREE_SKILL", expectedAmount: 1);
            });

    // ─── FlickFlack: 6 / 8 (AOE; single dummy still takes full hit) ───────
    private static Task<TestHelpers.TestResult> Test_FlickFlack()
        => TestHelpers.SingleCardTest<FlickFlack>(
            name: "FlickFlack deals 6 (AOE)",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 6, "damage"));

    private static Task<TestHelpers.TestResult> Test_FlickFlackPlus()
        => TestHelpers.SingleCardTest<FlickFlack>(
            name: "FlickFlack+ deals 8 (AOE)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 8, "damage"));

    // ─── LeadingStrike: 3 dmg + 2 Shivs / 6 dmg + 2 Shivs ─────────────────
    private static Task<TestHelpers.TestResult> Test_LeadingStrike()
        => TestHelpers.SingleCardTest<LeadingStrike>(
            name: "LeadingStrike deals 3 + 2 Shivs",
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 3) return $"expected damage=3, got {dmg}";
                // Two Shivs added to hand. Hand started with 0 (we removed LeadingStrike).
                // After play, hand should contain 2 Shivs.
                var shivCount = h.Player.PlayerCombatState!.Hand.Cards.Count(c => c.GetType().Name == "Shiv");
                if (shivCount != 2) return $"expected 2 Shivs in hand, got {shivCount}";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_LeadingStrikePlus()
        => TestHelpers.SingleCardTest<LeadingStrike>(
            name: "LeadingStrike+ deals 6 + 2 Shivs",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 6) return $"expected damage=6, got {dmg}";
                var shivCount = h.Player.PlayerCombatState!.Hand.Cards.Count(c => c.GetType().Name == "Shiv");
                if (shivCount != 2) return $"expected 2 Shivs in hand, got {shivCount}";
                return null;
            });

    // ─── EchoingSlash: 10 / 13 (one hit; AOE but dummy survives) ──────────
    private static Task<TestHelpers.TestResult> Test_EchoingSlash()
        => TestHelpers.SingleCardTest<EchoingSlash>(
            name: "EchoingSlash deals 10",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 10, "damage"));

    private static Task<TestHelpers.TestResult> Test_EchoingSlashPlus()
        => TestHelpers.SingleCardTest<EchoingSlash>(
            name: "EchoingSlash+ deals 13",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 13, "damage"));

    // ─── PreciseCut: 13 - 2*(other cards in hand). Empty hand → 13 / 16 ──
    private static Task<TestHelpers.TestResult> Test_PreciseCut()
        => TestHelpers.SingleCardTest<PreciseCut>(
            name: "PreciseCut deals 13 (empty hand)",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 13, "damage"));

    private static Task<TestHelpers.TestResult> Test_PreciseCutPlus()
        => TestHelpers.SingleCardTest<PreciseCut>(
            name: "PreciseCut+ deals 16 (empty hand)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 16, "damage"));

    // ─── Murder: 1 + 1*(cards drawn this combat). Harness draws 0 → 1 / 1 ─
    // Upgrade reduces cost from 3 to 2 — damage is unchanged.
    private static Task<TestHelpers.TestResult> Test_Murder()
        => TestHelpers.SingleCardTest<Murder>(
            name: "Murder deals 1 (no draws)",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 1, "damage"));

    private static Task<TestHelpers.TestResult> Test_MurderPlus()
        => TestHelpers.SingleCardTest<Murder>(
            name: "Murder+ deals 1 (no draws)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 1, "damage"));

    // ─── Flanking ─── MultiplayerOnly. FlankingPower.AfterApplied reads
    //   RunManager.Instance.NetService.Platform → NRE in headless.
    //   Skip until we shim RunManager or guard MP-only paths.
    private static Task<TestHelpers.TestResult> Test_Flanking()
        => TestHelpers.Skip("Flanking applies FlankingPower 2", "MultiplayerOnly: FlankingPower.AfterApplied reads RunManager.Instance.NetService.Platform which is null in headless. Needs RunManager shim.");

    private static Task<TestHelpers.TestResult> Test_FlankingPlus()
        => TestHelpers.Skip("Flanking+ applies FlankingPower 2 (cost reduced)", "MultiplayerOnly — same NRE as base Flanking.");

    // ─── FollowThrough: 7 / 9 (one hit when hand has <5 other cards) ──────
    private static Task<TestHelpers.TestResult> Test_FollowThrough()
        => TestHelpers.SingleCardTest<FollowThrough>(
            name: "FollowThrough deals 7 (no extra hand)",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 7, "damage"));

    private static Task<TestHelpers.TestResult> Test_FollowThroughPlus()
        => TestHelpers.SingleCardTest<FollowThrough>(
            name: "FollowThrough+ deals 9 (no extra hand)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));

    // ─── Backstab: 11 / 15 ────────────────────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Backstab()
        => TestHelpers.SingleCardTest<Backstab>(
            name: "Backstab deals 11",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 11, "damage"));

    private static Task<TestHelpers.TestResult> Test_BackstabPlus()
        => TestHelpers.SingleCardTest<Backstab>(
            name: "Backstab+ deals 15",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 15, "damage"));

    // ─── MementoMori: 9 + 4*discardsThisTurn / 11 + 5*discardsThisTurn ───
    // Empty turn history → 9 / 11.
    private static Task<TestHelpers.TestResult> Test_MementoMori()
        => TestHelpers.SingleCardTest<MementoMori>(
            name: "MementoMori deals 9 (no discards)",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));

    private static Task<TestHelpers.TestResult> Test_MementoMoriPlus()
        => TestHelpers.SingleCardTest<MementoMori>(
            name: "MementoMori+ deals 11 (no discards)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 11, "damage"));

    // ─── Neutralize: 3 dmg + Weak 1 / 4 dmg + Weak 2 ─────────────────────
    private static Task<TestHelpers.TestResult> Test_Neutralize()
        => TestHelpers.SingleCardTest<Neutralize>(
            name: "Neutralize deals 3 + Weak 1",
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 3) return $"expected damage=3, got {dmg}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_NeutralizePlus()
        => TestHelpers.SingleCardTest<Neutralize>(
            name: "Neutralize+ deals 4 + Weak 2",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 4) return $"expected damage=4, got {dmg}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 2);
            });

    // ─── DaggerThrow: 9 / 12 dmg, then draw 1 + force discard 1 ──────────
    // CardSelectCmd from empty hand should no-op (cardModel == null branch).
    private static Task<TestHelpers.TestResult> Test_DaggerThrow()
        => TestHelpers.SingleCardTest<DaggerThrow>(
            name: "DaggerThrow deals 9",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));

    private static Task<TestHelpers.TestResult> Test_DaggerThrowPlus()
        => TestHelpers.SingleCardTest<DaggerThrow>(
            name: "DaggerThrow+ deals 12",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 12, "damage"));
}
