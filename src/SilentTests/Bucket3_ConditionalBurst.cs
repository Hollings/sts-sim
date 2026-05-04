using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 3 — Conditional/Burst Attacks. Damage scales with combat state:
/// cards played this turn, attacks remaining, enemy HP %, etc.
///
/// Scope (12 cards × 2 upgrade levels = 24 tests):
///   Finisher, Assassinate, GrandFinale, KnifeTrap, Strangle, PoisonedStab,
///   Expose, Malaise, CorrosiveWave, Suppress, PiercingWail, LegSweep
/// </summary>
internal static class Bucket3_ConditionalBurst
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>();

        results.Add(await Test_Finisher());
        results.Add(await Test_FinisherPlus());
        results.Add(await Test_Assassinate());
        results.Add(await Test_AssassinatePlus());
        results.Add(await Test_GrandFinale());
        results.Add(await Test_GrandFinalePlus());
        results.Add(await Test_KnifeTrap());
        results.Add(await Test_KnifeTrapPlus());
        results.Add(await Test_Strangle());
        results.Add(await Test_StranglePlus());
        results.Add(await Test_PoisonedStab());
        results.Add(await Test_PoisonedStabPlus());
        results.Add(await Test_Expose());
        results.Add(await Test_ExposePlus());
        results.Add(await Test_Malaise());
        results.Add(await Test_MalaisePlus());
        results.Add(await Test_CorrosiveWave());
        results.Add(await Test_CorrosiveWavePlus());
        results.Add(await Test_Suppress());
        results.Add(await Test_SuppressPlus());
        results.Add(await Test_PiercingWail());
        results.Add(await Test_PiercingWailPlus());
        results.Add(await Test_LegSweep());
        results.Add(await Test_LegSweepPlus());

        return results;
    }

    // ─── Finisher ────────────────────────────────────────────────────────────
    // Hits N times for 6 (8 upgraded) where N = attacks finished this turn
    // (by the same owner). After 2 prior Strikes, expect 2 hits = 12 damage
    // (or 16 upgraded). Strike itself does 6, so total damage = 6 + 6 + 12 = 24.

    private static Task<TestHelpers.TestResult> Test_Finisher()
        => TestHelpers.SequenceTest(
            name: "Finisher hits once per attack played this turn (base 6 dmg)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(Finisher)),
            },
            energy: 99,
            assert: (h, before) =>
            {
                // Strike(6) + Strike(6) + Finisher(6 dmg × 2 attacks already played) = 24
                int dmg = before.dummyHp - h.Dummy.CurrentHp;
                return TestHelpers.Expect(dmg, 24, "total damage");
            });

    private static Task<TestHelpers.TestResult> Test_FinisherPlus()
        => TestHelpers.SequenceTest(
            name: "Finisher+ hits once per attack played this turn (base 8 dmg)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(Finisher), 1),
            },
            energy: 99,
            assert: (h, before) =>
            {
                // Strike(6) + Strike(6) + Finisher+(8 × 2) = 28
                int dmg = before.dummyHp - h.Dummy.CurrentHp;
                return TestHelpers.Expect(dmg, 28, "total damage");
            });

    // ─── Assassinate ─────────────────────────────────────────────────────────
    // 10 dmg + 1 Vulnerable. Upgraded: 13 dmg + 2 Vulnerable.

    private static Task<TestHelpers.TestResult> Test_Assassinate()
        => TestHelpers.SingleCardTest<Assassinate>(
            name: "Assassinate deals 10 + 1 Vulnerable",
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 10) return $"expected 10 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_AssassinatePlus()
        => TestHelpers.SingleCardTest<Assassinate>(
            name: "Assassinate+ deals 13 + 2 Vulnerable",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 13) return $"expected 13 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", expectedAmount: 2);
            });

    // ─── GrandFinale ─────────────────────────────────────────────────────────
    // 60 dmg AOE only when draw pile is empty. Upgraded: 75.
    // SingleCardTest with no extras puts the only card (GrandFinale) in hand,
    // leaving the draw pile empty — so it should be playable.

    private static Task<TestHelpers.TestResult> Test_GrandFinale()
        => TestHelpers.SingleCardTest<GrandFinale>(
            name: "GrandFinale deals 60 AOE when draw pile is empty",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 60, "damage"));

    private static Task<TestHelpers.TestResult> Test_GrandFinalePlus()
        => TestHelpers.SingleCardTest<GrandFinale>(
            name: "GrandFinale+ deals 75 AOE when draw pile is empty",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 75, "damage"));

    // ─── KnifeTrap ───────────────────────────────────────────────────────────
    // Re-plays every Shiv currently in your Exhaust pile at the target.
    // With no Shivs in exhaust, deals 0 damage and applies nothing.
    // We test this minimal case since seeding the exhaust pile would require
    // more harness machinery than SingleCardTest exposes.

    private static Task<TestHelpers.TestResult> Test_KnifeTrap()
        => TestHelpers.SingleCardTest<KnifeTrap>(
            name: "KnifeTrap with no shivs in exhaust deals 0",
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 0, "damage"));

    private static Task<TestHelpers.TestResult> Test_KnifeTrapPlus()
        => TestHelpers.SingleCardTest<KnifeTrap>(
            name: "KnifeTrap+ with no shivs in exhaust deals 0",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 0, "damage"));

    // ─── Strangle ────────────────────────────────────────────────────────────
    // 8 dmg + applies StranglePower(2). Upgraded: 10 dmg + StranglePower(3).
    // StranglePower itself triggers on next card play to deal counter damage,
    // but we just verify the immediate hit + power application here.

    private static Task<TestHelpers.TestResult> Test_Strangle()
        => TestHelpers.SingleCardTest<Strangle>(
            name: "Strangle deals 8 + applies StranglePower(2)",
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 8) return $"expected 8 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "STRANGLE", expectedAmount: 2);
            });

    private static Task<TestHelpers.TestResult> Test_StranglePlus()
        => TestHelpers.SingleCardTest<Strangle>(
            name: "Strangle+ deals 10 + applies StranglePower(3)",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 10) return $"expected 10 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "STRANGLE", expectedAmount: 3);
            });

    // ─── PoisonedStab ────────────────────────────────────────────────────────
    // 6 dmg + 3 Poison. Upgraded: 8 dmg + 4 Poison.

    private static Task<TestHelpers.TestResult> Test_PoisonedStab()
        => TestHelpers.SingleCardTest<PoisonedStab>(
            name: "PoisonedStab deals 6 + 3 Poison",
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 6) return $"expected 6 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 3);
            });

    private static Task<TestHelpers.TestResult> Test_PoisonedStabPlus()
        => TestHelpers.SingleCardTest<PoisonedStab>(
            name: "PoisonedStab+ deals 8 + 4 Poison",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 8) return $"expected 8 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 4);
            });

    // ─── Expose ──────────────────────────────────────────────────────────────
    // Removes target's block, removes Artifact, applies 2 Vulnerable.
    // Upgraded: applies 3 Vulnerable. Dummy has 0 block / no artifact by
    // default, so just verify Vulnerable application (no damage).

    private static Task<TestHelpers.TestResult> Test_Expose()
        => TestHelpers.SingleCardTest<Expose>(
            name: "Expose applies 2 Vulnerable",
            assert: (h, before) =>
            {
                if (before.dummyHp != h.Dummy.CurrentHp) return $"expected no damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", expectedAmount: 2);
            });

    private static Task<TestHelpers.TestResult> Test_ExposePlus()
        => TestHelpers.SingleCardTest<Expose>(
            name: "Expose+ applies 3 Vulnerable",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp != h.Dummy.CurrentHp) return $"expected no damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", expectedAmount: 3);
            });

    // ─── Malaise ─────────────────────────────────────────────────────────────
    // X-cost: applies -X Strength + X Weak (X+1 each upgraded).
    // Malaise: X-cost. Apply -X Strength + X Weak to enemy. Upgraded: -X-1 / X+1.
    // PlayCard now captures CapturedXValue from current energy (3 by default) → X=3.
    private static Task<TestHelpers.TestResult> Test_Malaise()
        => TestHelpers.SingleCardTest<Malaise>(
            name: "Malaise (X=3): -3 Strength + 3 Weak on enemy",
            assert: (h, before) =>
            {
                var weak = TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 3);
                if (weak != null) return weak;
                // Strength can be negative. ExpectPower with negative amount works fine.
                return TestHelpers.ExpectPower(h.Dummy, "STRENGTH", expectedAmount: -3);
            });

    private static Task<TestHelpers.TestResult> Test_MalaisePlus()
        => TestHelpers.SingleCardTest<Malaise>(
            name: "Malaise+ (X=3): -4 Strength + 4 Weak on enemy",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var weak = TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 4);
                if (weak != null) return weak;
                return TestHelpers.ExpectPower(h.Dummy, "STRENGTH", expectedAmount: -4);
            });

    // ─── CorrosiveWave ───────────────────────────────────────────────────────
    // Applies CorrosiveWavePower(2) to self. Upgraded: 3.

    private static Task<TestHelpers.TestResult> Test_CorrosiveWave()
        => TestHelpers.SingleCardTest<CorrosiveWave>(
            name: "CorrosiveWave applies CorrosiveWavePower(2) to self",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "CORROSIVE", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_CorrosiveWavePlus()
        => TestHelpers.SingleCardTest<CorrosiveWave>(
            name: "CorrosiveWave+ applies CorrosiveWavePower(3) to self",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "CORROSIVE", expectedAmount: 3));

    // ─── Suppress ────────────────────────────────────────────────────────────
    // 11 dmg + 3 Weak. Upgraded: 17 dmg + 5 Weak.

    private static Task<TestHelpers.TestResult> Test_Suppress()
        => TestHelpers.SingleCardTest<Suppress>(
            name: "Suppress deals 11 + 3 Weak",
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 11) return $"expected 11 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 3);
            });

    private static Task<TestHelpers.TestResult> Test_SuppressPlus()
        => TestHelpers.SingleCardTest<Suppress>(
            name: "Suppress+ deals 17 + 5 Weak",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                if (before.dummyHp - h.Dummy.CurrentHp != 17) return $"expected 17 damage, got {before.dummyHp - h.Dummy.CurrentHp}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 5);
            });

    // ─── PiercingWail ────────────────────────────────────────────────────────
    // Applies PiercingWailPower(6) to all enemies. Upgraded: 8.
    // Single dummy, so only one application to verify.

    private static Task<TestHelpers.TestResult> Test_PiercingWail()
        => TestHelpers.SingleCardTest<PiercingWail>(
            name: "PiercingWail applies PiercingWailPower(6) to enemy",
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "PIERCING", expectedAmount: 6));

    private static Task<TestHelpers.TestResult> Test_PiercingWailPlus()
        => TestHelpers.SingleCardTest<PiercingWail>(
            name: "PiercingWail+ applies PiercingWailPower(8) to enemy",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "PIERCING", expectedAmount: 8));

    // ─── LegSweep ────────────────────────────────────────────────────────────
    // 11 block (self) + 2 Weak (target). Upgraded: 14 block + 3 Weak.

    private static Task<TestHelpers.TestResult> Test_LegSweep()
        => TestHelpers.SingleCardTest<LegSweep>(
            name: "LegSweep grants 11 block + applies 2 Weak",
            assert: (h, before) =>
            {
                int blockGain = h.Player.Creature.Block - before.playerBlock;
                if (blockGain != 11) return $"expected 11 block, got {blockGain}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 2);
            });

    private static Task<TestHelpers.TestResult> Test_LegSweepPlus()
        => TestHelpers.SingleCardTest<LegSweep>(
            name: "LegSweep+ grants 14 block + applies 3 Weak",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                int blockGain = h.Player.Creature.Block - before.playerBlock;
                if (blockGain != 14) return $"expected 14 block, got {blockGain}";
                return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 3);
            });
}
