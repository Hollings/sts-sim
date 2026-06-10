using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Orbs;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Defect battery. Focused on the orb pipeline — the only character mechanic
/// that lives outside the card/power/relic hook flow the other batteries
/// already exercise: channel/evoke, queue capacity + overflow, end-of-turn
/// passives (which TurnHooks must fire by hand), Focus modification, and the
/// X-cost evoke (MultiCast). Cards chosen for fragility, not coverage.
/// </summary>
internal static class DefectTests
{
    private static readonly Type[] Fodder = { typeof(StrikeDefect), typeof(StrikeDefect), typeof(StrikeDefect) };

    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            // Baseline sanity: the character boots and a plain attack lands.
            await CharTestHelpers.Play1<Defect, StrikeDefect>(
                "StrikeDefect deals 6",
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 6, "damage")),

            // Player.CreateForNewRun must carry the character's BaseOrbSlotCount
            // into the OrbQueue, or nothing below can channel at all.
            await CharTestHelpers.Test<Defect>(
                "Defect starts with 3 orb slots",
                new List<Harness.DeckEntry> { new(typeof(StrikeDefect)) },
                h => Task.FromResult(TestHelpers.Expect(
                    h.Player.PlayerCombatState!.OrbQueue.Capacity, 3, "orb capacity"))),

            await CharTestHelpers.Play1<Defect, Zap>(
                "Zap channels a Lightning orb",
                (h, b) =>
                {
                    var orbs = h.Player.PlayerCombatState!.OrbQueue.Orbs;
                    if (orbs.Count != 1) return $"expected 1 orb, got {orbs.Count}";
                    return orbs[0] is LightningOrb ? null : $"expected LightningOrb, got {orbs[0].GetType().Name}";
                }),

            await CharTestHelpers.Play1<Defect, BallLightning>(
                "BallLightning deals 7 and channels Lightning",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 7, "damage")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Orbs.Count, 1, "orbs")),

            await CharTestHelpers.Play1<Defect, Coolheaded>(
                "Coolheaded channels Frost and draws 1",
                (h, b) =>
                {
                    var orbs = h.Player.PlayerCombatState!.OrbQueue.Orbs;
                    if (orbs.Count != 1 || orbs[0] is not FrostOrb)
                        return $"expected 1 FrostOrb, got [{string.Join(",", orbs.Select(o => o.GetType().Name))}]";
                    // Hand: -1 played, +1 drawn → unchanged.
                    return TestHelpers.Expect(h.Player.PlayerCombatState!.Hand.Cards.Count, b.handSize, "hand size");
                },
                extraDeckCards: Fodder),

            await CharTestHelpers.Play1<Defect, Glacier>(
                "Glacier gives 6 block and channels 2 Frost",
                (h, b) =>
                    TestHelpers.Expect(h.Player.Creature.Block - b.playerBlock, 6, "block")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Orbs.Count(o => o is FrostOrb), 2, "frost orbs")),

            // Dualcast evokes the front orb twice (once without dequeue, once with).
            await CharTestHelpers.PlaySeq<Defect>(
                "Zap then Dualcast deals 16 (8x2 evoke) and empties queue",
                new List<Harness.DeckEntry> { new(typeof(Zap)), new(typeof(Dualcast)) },
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 16, "damage")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Orbs.Count, 0, "orbs left")),

            // End-of-turn passives only exist because TurnHooks fires
            // OrbQueue.BeforeTurnEnd by hand — these two guard that wiring.
            await CharTestHelpers.Test<Defect>(
                "Lightning passive zaps 3 at end of turn",
                new List<Harness.DeckEntry> { new(typeof(Zap)) },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    var zap = CharTestHelpers.MoveToHand(h, typeof(Zap))!;
                    await CharTestHelpers.PlayCard(h, zap);
                    var before = h.Dummy.CurrentHp;
                    await TurnHooks.EndOfPlayerTurn(h);
                    return TestHelpers.Expect(before - h.Dummy.CurrentHp, 3, "passive damage")
                        ?? TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Orbs.Count, 1, "orb retained");
                }),

            await CharTestHelpers.Test<Defect>(
                "Frost passive grants 2 block at end of turn",
                new List<Harness.DeckEntry> { new(typeof(Coolheaded)), new(typeof(StrikeDefect)) },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    var cool = CharTestHelpers.MoveToHand(h, typeof(Coolheaded))!;
                    await CharTestHelpers.PlayCard(h, cool);
                    var blockBefore = h.Player.Creature.Block;
                    await TurnHooks.EndOfPlayerTurn(h);
                    return TestHelpers.Expect(h.Player.Creature.Block - blockBefore, 2, "frost block");
                }),

            // Dark accumulates its evoke value each turn-end passive tick.
            await CharTestHelpers.Test<Defect>(
                "Dark orb accumulates then evokes for 12",
                new List<Harness.DeckEntry> { new(typeof(StrikeDefect)) },
                async h =>
                {
                    await OrbCmd.Channel<DarkOrb>(h.Ctx, h.Player);
                    await TurnHooks.EndOfPlayerTurn(h); // passive: evoke value 6 → 12
                    var before = h.Dummy.CurrentHp;
                    await OrbCmd.EvokeNext(h.Ctx, h.Player);
                    return TestHelpers.Expect(before - h.Dummy.CurrentHp, 12, "dark evoke");
                }),

            await CharTestHelpers.Play1<Defect, Defragment>(
                "Defragment applies Focus 1",
                (h, b) => TestHelpers.ExpectPower(h.Player.Creature, "FOCUS", 1)),

            // Focus must flow through Hook.ModifyOrbValue into evoke values.
            await CharTestHelpers.PlaySeq<Defect>(
                "Focus 1 boosts Lightning evoke to 9 (Dualcast → 18)",
                new List<Harness.DeckEntry> { new(typeof(Defragment)), new(typeof(Zap)), new(typeof(Dualcast)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 18, "damage")),

            await CharTestHelpers.Play1<Defect, Capacitor>(
                "Capacitor adds 2 orb slots (3 → 5)",
                (h, b) => TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Capacity, 5, "capacity")),

            // Channeling into a full queue auto-evokes the front orb.
            await CharTestHelpers.PlaySeq<Defect>(
                "4th channel on 3 slots evokes front Lightning (8 dmg)",
                new List<Harness.DeckEntry> { new(typeof(Zap)), new(typeof(Zap)), new(typeof(Zap)), new(typeof(Zap)) },
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 8, "overflow evoke damage")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.OrbQueue.Orbs.Count, 3, "orbs")),

            // X-cost evoke: X is captured by SpendResources from current energy.
            await CharTestHelpers.Test<Defect>(
                "MultiCast X=2 evokes front orb twice (16 dmg, 1 orb left)",
                new List<Harness.DeckEntry> { new(typeof(Zap)), new(typeof(Zap)), new(typeof(MultiCast)) },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    foreach (var t in new[] { typeof(Zap), typeof(Zap) })
                        await CharTestHelpers.PlayCard(h, CharTestHelpers.MoveToHand(h, t)!);
                    var multi = CharTestHelpers.MoveToHand(h, typeof(MultiCast))!;
                    Reflect.SetEnergy(pcs, 2);
                    var before = h.Dummy.CurrentHp;
                    await CharTestHelpers.PlayCard(h, multi);
                    return TestHelpers.Expect(before - h.Dummy.CurrentHp, 16, "evoke damage")
                        ?? TestHelpers.Expect(pcs.OrbQueue.Orbs.Count, 1, "orbs left")
                        ?? TestHelpers.Expect(pcs.Energy, 0, "energy after X spend");
                }),

            await CharTestHelpers.PlaySeq<Defect>(
                "Barrage hits once per channeled orb (2 orbs → 10 dmg)",
                new List<Harness.DeckEntry> { new(typeof(Zap)), new(typeof(Zap)), new(typeof(Barrage)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 10, "damage")),

            // Claw self-buffs every Claw in the combat on play.
            await CharTestHelpers.PlaySeq<Defect>(
                "Claw scales: 3 then 5 (total 8)",
                new List<Harness.DeckEntry> { new(typeof(Claw)), new(typeof(Claw)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 8, "total damage")),

            // Starter relic: Cracked Core channels 1 Lightning during the
            // turn-1 side-turn-start hooks.
            await CharTestHelpers.Test<Defect>(
                "Cracked Core channels Lightning on turn 1",
                new List<Harness.DeckEntry> { new(typeof(StrikeDefect)) },
                async h =>
                {
                    await TurnHooks.PrepareSideTurnStart(h, 1);
                    var orbs = h.Player.PlayerCombatState!.OrbQueue.Orbs;
                    return orbs.Count == 1 && orbs[0] is LightningOrb
                        ? null
                        : $"expected 1 LightningOrb, got [{string.Join(",", orbs.Select(o => o.GetType().Name))}]";
                }),
        };

        return results;
    }
}
