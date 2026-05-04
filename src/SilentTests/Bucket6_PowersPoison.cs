using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 6 — Powers & Poison Suite. Permanent combat powers (Envenom,
/// Footwork, etc.) and the poison-stack family. Tests need to apply the
/// power, then play another card to verify the effect (or watch poison tick).
///
/// Scope (14 cards × 2 upgrade levels = 28 tests):
///   Envenom, NoxiousFumes, Footwork, BulletTime, Burst, WraithForm,
///   SerpentForm, InfiniteBlades, Afterimage, DeadlyPoison, BouncingFlask,
///   BubbleBubble, Untouchable, CorrosiveWave (also in B3, but the poison
///   side belongs here)
/// </summary>
internal static class Bucket6_PowersPoison
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>();

        results.Add(await Test_Envenom());
        results.Add(await Test_EnvenomPlus());
        results.Add(await Test_NoxiousFumes());
        results.Add(await Test_NoxiousFumesPlus());
        results.Add(await Test_Footwork());
        results.Add(await Test_FootworkPlus());
        results.Add(await Test_BulletTime());
        results.Add(await Test_BulletTimePlus());
        results.Add(await Test_Burst());
        results.Add(await Test_BurstPlus());
        results.Add(await Test_WraithForm());
        results.Add(await Test_WraithFormPlus());
        results.Add(await Test_SerpentForm());
        results.Add(await Test_SerpentFormPlus());
        results.Add(await Test_InfiniteBlades());
        results.Add(await Test_InfiniteBladesPlus());
        results.Add(await Test_Afterimage());
        results.Add(await Test_AfterimagePlus());
        results.Add(await Test_DeadlyPoison());
        results.Add(await Test_DeadlyPoisonPlus());
        results.Add(await Test_BouncingFlask());
        results.Add(await Test_BouncingFlaskPlus());
        results.Add(await Test_BubbleBubble());
        results.Add(await Test_BubbleBubblePlus());
        results.Add(await Test_Untouchable());
        results.Add(await Test_UntouchablePlus());
        results.Add(await Test_CorrosiveWave());
        results.Add(await Test_CorrosiveWavePlus());

        return results;
    }

    // ─── Envenom ─── Power: when you deal unblocked attack damage, apply 1
    //   (or 2 upgraded) poison. Test by playing Envenom, then a Strike.

    private static Task<TestHelpers.TestResult> Test_Envenom()
        => TestHelpers.SequenceTest(
            name: "Envenom: poison applied after Strike (1)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Envenom)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
            },
            assert: (h, before) =>
            {
                var powerErr = TestHelpers.ExpectPower(h.Player.Creature, "ENVENOM", expectedAmount: 1);
                if (powerErr != null) return powerErr;
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_EnvenomPlus()
        => TestHelpers.SequenceTest(
            name: "Envenom+: poison applied after Strike (2)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Envenom), 1),
                new Harness.DeckEntry(typeof(StrikeSilent)),
            },
            assert: (h, before) =>
            {
                var powerErr = TestHelpers.ExpectPower(h.Player.Creature, "ENVENOM", expectedAmount: 2);
                if (powerErr != null) return powerErr;
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 2);
            });

    // ─── NoxiousFumes ─── Power: at start of player turn, apply 2 (3 upgraded)
    //   poison to all enemies. Trigger via FireAfterSideTurnStart(Player).

    private static Task<TestHelpers.TestResult> Test_NoxiousFumes()
        => TestHelpers.SingleCardTest<NoxiousFumes>(
            name: "NoxiousFumes: 2 poison on enemy after side-turn-start fires",
            assert: (h, before) =>
            {
                var powerErr = TestHelpers.ExpectPower(h.Player.Creature, "NOXIOUS", expectedAmount: 2);
                if (powerErr != null) return powerErr;
                TurnHooks.FireAfterSideTurnStart(h, CombatSide.Player).GetAwaiter().GetResult();
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 2);
            });

    private static Task<TestHelpers.TestResult> Test_NoxiousFumesPlus()
        => TestHelpers.SingleCardTest<NoxiousFumes>(
            name: "NoxiousFumes+: 3 poison on enemy after side-turn-start fires",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var powerErr = TestHelpers.ExpectPower(h.Player.Creature, "NOXIOUS", expectedAmount: 3);
                if (powerErr != null) return powerErr;
                TurnHooks.FireAfterSideTurnStart(h, CombatSide.Player).GetAwaiter().GetResult();
                return TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 3);
            });

    // ─── Footwork ─── Apply +2 (+3 upgraded) Dexterity to self.

    private static Task<TestHelpers.TestResult> Test_Footwork()
        => TestHelpers.SingleCardTest<Footwork>(
            name: "Footwork: +2 Dexterity",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "DEXTERITY", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_FootworkPlus()
        => TestHelpers.SingleCardTest<Footwork>(
            name: "Footwork+: +3 Dexterity",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "DEXTERITY", expectedAmount: 3));

    // ─── BulletTime ─── Apply NoDrawPower 1, also sets every other hand card
    //   to free this turn. Upgrade reduces energy cost by 1 (3 → 2).

    private static Task<TestHelpers.TestResult> Test_BulletTime()
        => TestHelpers.SingleCardTest<BulletTime>(
            name: "BulletTime: NoDraw applied",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "NODRAW", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_BulletTimePlus()
        => TestHelpers.SingleCardTest<BulletTime>(
            name: "BulletTime+: NoDraw applied (cost 2)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "NODRAW", expectedAmount: 1));

    // ─── Burst ─── Apply BurstPower N: next N SKILLS (not Powers!) are played twice.
    //   Footwork is a Power, so it doesn't trigger Burst. Use Defend (Skill, +5 block):
    //   Burst then Defend → Defend plays twice → 10 block.
    //   Burst+ has 2 charges, so Defend+Defend → 20 block (each Defend doubles).

    private static Task<TestHelpers.TestResult> Test_Burst()
        => TestHelpers.SequenceTest(
            name: "Burst: next skill plays twice (Defend → 10 block)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Burst)),
                new Harness.DeckEntry(typeof(DefendSilent)),
            },
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 10, "block (5 × 2 plays)"));

    private static Task<TestHelpers.TestResult> Test_BurstPlus()
        => TestHelpers.SequenceTest(
            name: "Burst+: next 2 skills each play twice (Defend ×2 → 20 block)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Burst), 1),
                new Harness.DeckEntry(typeof(DefendSilent)),
                new Harness.DeckEntry(typeof(DefendSilent)),
            },
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 20, "block (2 Defends × 2 plays each)"));

    // ─── WraithForm ─── Power: gain 2 (3 upgraded) Intangible + WraithFormPower 1.

    private static Task<TestHelpers.TestResult> Test_WraithForm()
        => TestHelpers.SingleCardTest<WraithForm>(
            name: "WraithForm: +2 Intangible + WraithForm power",
            assert: (h, before) =>
            {
                var intang = TestHelpers.ExpectPower(h.Player.Creature, "INTANGIBLE", expectedAmount: 2);
                if (intang != null) return intang;
                return TestHelpers.ExpectPower(h.Player.Creature, "WRAITHFORM", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_WraithFormPlus()
        => TestHelpers.SingleCardTest<WraithForm>(
            name: "WraithForm+: +3 Intangible + WraithForm power",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                var intang = TestHelpers.ExpectPower(h.Player.Creature, "INTANGIBLE", expectedAmount: 3);
                if (intang != null) return intang;
                return TestHelpers.ExpectPower(h.Player.Creature, "WRAITHFORM", expectedAmount: 1);
            });

    // ─── SerpentForm ─── Power: SerpentFormPower 4 (6 upgraded). Each card
    //   played hits a random enemy for N. Just verify the power lands.

    private static Task<TestHelpers.TestResult> Test_SerpentForm()
        => TestHelpers.SingleCardTest<SerpentForm>(
            name: "SerpentForm: SerpentForm power 4",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "SERPENT", expectedAmount: 4));

    private static Task<TestHelpers.TestResult> Test_SerpentFormPlus()
        => TestHelpers.SingleCardTest<SerpentForm>(
            name: "SerpentForm+: SerpentForm power 6",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "SERPENT", expectedAmount: 6));

    // ─── InfiniteBlades ─── Power: InfiniteBladesPower 1. At start of each
    //   draw, generate a Shiv. Verify the power applies (Shiv generation
    //   requires a draw cycle to fire).

    private static Task<TestHelpers.TestResult> Test_InfiniteBlades()
        => TestHelpers.SingleCardTest<InfiniteBlades>(
            name: "InfiniteBlades: power applied",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "INFINITEBLADES", expectedAmount: 1));

    private static Task<TestHelpers.TestResult> Test_InfiniteBladesPlus()
        => TestHelpers.SingleCardTest<InfiniteBlades>(
            name: "InfiniteBlades+: power applied (Innate added)",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "INFINITEBLADES", expectedAmount: 1));

    // ─── Afterimage ─── Power: gain 1 block whenever a card is played.
    //   Test by playing Afterimage then a Defend; we expect the Defend to
    //   give its normal block PLUS 1 extra from Afterimage triggering on
    //   AfterimagePower hooks BeforeCardPlayed (records amount) and AfterCardPlayed
    //   (grants block). BeforeCardPlayed fires BEFORE OnPlay runs, so when Afterimage
    //   itself is played, its OWN BeforeCardPlayed fires before AfterimagePower exists
    //   → no record for itself → no block from its own play. Subsequent cards do
    //   trigger it. Two follow-up Strikes → 2 block.

    private static Task<TestHelpers.TestResult> Test_Afterimage()
        => TestHelpers.SequenceTest(
            name: "Afterimage: +1 block per card played AFTER it (2 Strikes → 2 block)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Afterimage)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
            },
            assert: (h, before) =>
            {
                var pwr = TestHelpers.ExpectPower(h.Player.Creature, "AFTERIMAGE", expectedAmount: 1);
                if (pwr != null) return pwr;
                return TestHelpers.Expect(h.Player.Creature.Block, 2, "block (1 per Strike)");
            });

    private static Task<TestHelpers.TestResult> Test_AfterimagePlus()
        => TestHelpers.SequenceTest(
            name: "Afterimage+: same effect (2 Strikes → 2 block, plus Innate keyword)",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(Afterimage), 1),
                new Harness.DeckEntry(typeof(StrikeSilent)),
                new Harness.DeckEntry(typeof(StrikeSilent)),
            },
            assert: (h, before) =>
            {
                var pwr = TestHelpers.ExpectPower(h.Player.Creature, "AFTERIMAGE", expectedAmount: 1);
                if (pwr != null) return pwr;
                return TestHelpers.Expect(h.Player.Creature.Block, 2, "block");
            });

    // ─── DeadlyPoison ─── Apply 5 (7 upgraded) poison to one enemy.

    private static Task<TestHelpers.TestResult> Test_DeadlyPoison()
        => TestHelpers.SingleCardTest<DeadlyPoison>(
            name: "DeadlyPoison: 5 poison on enemy",
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 5));

    private static Task<TestHelpers.TestResult> Test_DeadlyPoisonPlus()
        => TestHelpers.SingleCardTest<DeadlyPoison>(
            name: "DeadlyPoison+: 7 poison on enemy",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 7));

    // ─── BouncingFlask ─── Apply 3 poison N times to random enemies.
    //   Repeat = 3 base, 4 upgraded. Single dummy = stacked.

    private static Task<TestHelpers.TestResult> Test_BouncingFlask()
        => TestHelpers.SingleCardTest<BouncingFlask>(
            name: "BouncingFlask: 3 poison ×3 on dummy = 9",
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 9));

    private static Task<TestHelpers.TestResult> Test_BouncingFlaskPlus()
        => TestHelpers.SingleCardTest<BouncingFlask>(
            name: "BouncingFlask+: 3 poison ×4 on dummy = 12",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 12));

    // ─── BubbleBubble ─── If target has Poison, apply 9 (12 upgraded) more.
    //   Otherwise, no-op. Sequence: apply DeadlyPoison first to seed poison,
    //   then play BubbleBubble — total should be 5 + 9 = 14 (or 7 + 12 = 19).

    private static Task<TestHelpers.TestResult> Test_BubbleBubble()
        => TestHelpers.SequenceTest(
            name: "BubbleBubble (after DeadlyPoison): 5 + 9 = 14 poison",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(DeadlyPoison)),
                new Harness.DeckEntry(typeof(BubbleBubble)),
            },
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 14));

    private static Task<TestHelpers.TestResult> Test_BubbleBubblePlus()
        => TestHelpers.SequenceTest(
            name: "BubbleBubble+ (after DeadlyPoison): 5 + 12 = 17 poison",
            sequence: new[]
            {
                new Harness.DeckEntry(typeof(DeadlyPoison)),
                new Harness.DeckEntry(typeof(BubbleBubble), 1),
            },
            assert: (h, before) => TestHelpers.ExpectPower(h.Dummy, "POISON", expectedAmount: 17));

    // ─── Untouchable ─── Skill, gain 6 (8 upgraded) block. Sly keyword.

    private static Task<TestHelpers.TestResult> Test_Untouchable()
        => TestHelpers.SingleCardTest<Untouchable>(
            name: "Untouchable: 6 block",
            assert: (h, before) => TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 6, "block"));

    private static Task<TestHelpers.TestResult> Test_UntouchablePlus()
        => TestHelpers.SingleCardTest<Untouchable>(
            name: "Untouchable+: 8 block",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(h.Player.Creature.Block - before.playerBlock, 8, "block"));

    // ─── CorrosiveWave ─── Skill that applies CorrosiveWavePower 2 (3 upgraded)
    //   to self. Just verify the power lands.

    private static Task<TestHelpers.TestResult> Test_CorrosiveWave()
        => TestHelpers.SingleCardTest<CorrosiveWave>(
            name: "CorrosiveWave: power 2 on self",
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "CORROSIVE", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_CorrosiveWavePlus()
        => TestHelpers.SingleCardTest<CorrosiveWave>(
            name: "CorrosiveWave+: power 3 on self",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.ExpectPower(h.Player.Creature, "CORROSIVE", expectedAmount: 3));
}
