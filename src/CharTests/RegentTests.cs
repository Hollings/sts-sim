using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Regent battery. Focused on the star resource — the second currency that
/// only this character uses, debited by CardModel.SpendResources (which is why
/// these tests play through the same SpendResources path the sims use) — plus
/// the Forge/SovereignBlade token system and two history/draw-driven scalers
/// (Radiate, Kingly Punch) that exercise CombatHistory and the AfterCardDrawn
/// hook chain.
/// </summary>
internal static class RegentTests
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            await CharTestHelpers.Play1<Regent, StrikeRegent>(
                "StrikeRegent deals 6",
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 6, "damage")),

            await CharTestHelpers.Play1<Regent, Venerate>(
                "Venerate gains 2 stars",
                (h, b) => TestHelpers.Expect(h.Player.PlayerCombatState!.Stars - b.stars, 2, "stars gained")),

            await CharTestHelpers.Play1<Regent, GatherLight>(
                "Gather Light gives 8 block and 1 star",
                (h, b) =>
                    TestHelpers.Expect(h.Player.Creature.Block - b.playerBlock, 8, "block")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.Stars - b.stars, 1, "stars")),

            // Starter relic: 3 stars on entering a combat room.
            await CharTestHelpers.Test<Regent>(
                "Divine Right grants 3 stars on room enter",
                new List<Harness.DeckEntry> { new(typeof(StrikeRegent)) },
                async h =>
                {
                    await TurnHooks.FireAfterRoomEntered(h);
                    return TestHelpers.Expect(h.Player.PlayerCombatState!.Stars, 3, "stars");
                }),

            // Star cost debited through SpendResources, not energy.
            await CharTestHelpers.Play1<Regent, FallingStar>(
                "Falling Star (2 stars) deals 8, applies Weak+Vuln, debits stars",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 8, "damage")
                    ?? TestHelpers.ExpectPower(h.Dummy, "WEAK", 1)
                    ?? TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", 1)
                    ?? TestHelpers.Expect(b.stars - h.Player.PlayerCombatState!.Stars, 2, "stars spent"),
                setup: h => { h.Player.PlayerCombatState!.GainStars(3); return Task.CompletedTask; }),

            await CharTestHelpers.Play1<Regent, SevenStars>(
                "Seven Stars (7 stars) deals 7x7 = 49",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 49, "damage")
                    ?? TestHelpers.Expect(h.Player.PlayerCombatState!.Stars, 0, "stars left"),
                energy: 2,
                setup: h => { h.Player.PlayerCombatState!.GainStars(7); return Task.CompletedTask; }),

            // Comet's missile VFX goes through the NCombatRoom stub (null node path).
            await CharTestHelpers.Play1<Regent, Comet>(
                "Comet (5 stars) deals 33, applies Weak 3 + Vuln 3",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 33, "damage")
                    ?? TestHelpers.ExpectPower(h.Dummy, "WEAK", 3)
                    ?? TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", 3),
                setup: h => { h.Player.PlayerCombatState!.GainStars(5); return Task.CompletedTask; }),

            // Kingly Punch grows +4 every time it's drawn (AfterCardDrawn chain).
            await CharTestHelpers.Test<Regent>(
                "Kingly Punch deals 12 after being drawn once",
                new List<Harness.DeckEntry>
                {
                    new(typeof(KinglyPunch)), new(typeof(StrikeRegent)), new(typeof(StrikeRegent)),
                    new(typeof(StrikeRegent)), new(typeof(StrikeRegent)),
                },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    // Draw the whole 5-card deck through the real draw pipeline
                    // so Hook.AfterCardDrawn fires on the card model.
                    await TurnHooks.PlayerTurnStartDraw(h, 5);
                    var punch = pcs.Hand.Cards.OfType<KinglyPunch>().FirstOrDefault();
                    if (punch == null) return "Kingly Punch not drawn into hand";
                    var before = h.Dummy.CurrentHp;
                    await CharTestHelpers.PlayCard(h, punch);
                    return TestHelpers.Expect(before - h.Dummy.CurrentHp, 12, "damage (8 + 4 from draw)");
                }),

            // Radiate reads CombatHistory's StarsModified entries for this turn.
            await CharTestHelpers.PlaySeq<Regent>(
                "Radiate hits once per star gained this turn (4 stars → 12 dmg)",
                new List<Harness.DeckEntry> { new(typeof(Venerate)), new(typeof(Venerate)), new(typeof(Radiate)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 12, "damage (3x4 hits)")),

            // Forge: creates a Sovereign Blade token in hand and buffs it.
            await CharTestHelpers.Play1<Regent, BigBang>(
                "Big Bang forges a 15-damage Sovereign Blade, +1 energy, +1 star, draws 1",
                (h, b) =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    var blade = pcs.Hand.Cards.OfType<SovereignBlade>().FirstOrDefault();
                    if (blade == null) return "no Sovereign Blade in hand after Big Bang";
                    return TestHelpers.Expect((int)blade.DynamicVars.Damage.BaseValue, 15, "blade damage (10 base + 5 forge)")
                        ?? TestHelpers.Expect(pcs.Energy - b.energy, 1, "energy gained")
                        ?? TestHelpers.Expect(pcs.Stars - b.stars, 1, "stars gained");
                },
                extraDeckCards: new[] { typeof(StrikeRegent), typeof(StrikeRegent) },
                energy: 3),

            // The blade itself is playable and hits for its forged damage.
            await CharTestHelpers.Test<Regent>(
                "Sovereign Blade attacks for its forged damage (15)",
                new List<Harness.DeckEntry> { new(typeof(StrikeRegent)) },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    await ForgeCmd.Forge(5, h.Player, null);
                    var blade = pcs.Hand.Cards.OfType<SovereignBlade>().FirstOrDefault();
                    if (blade == null) return "Forge did not create a blade in hand";
                    var before = h.Dummy.CurrentHp;
                    await CharTestHelpers.PlayCard(h, blade);
                    return TestHelpers.Expect(before - h.Dummy.CurrentHp, 15, "blade damage");
                }),

            // Forging again upgrades the existing blade instead of adding one.
            await CharTestHelpers.Test<Regent>(
                "Second Forge buffs the same blade to 20 (no duplicate)",
                new List<Harness.DeckEntry> { new(typeof(StrikeRegent)) },
                async h =>
                {
                    await ForgeCmd.Forge(5, h.Player, null);
                    await ForgeCmd.Forge(5, h.Player, null);
                    var blades = h.Player.PlayerCombatState!.AllCards.OfType<SovereignBlade>().ToList();
                    return TestHelpers.Expect(blades.Count, 1, "blade count")
                        ?? TestHelpers.Expect((int)blades[0].DynamicVars.Damage.BaseValue, 20, "blade damage");
                }),
        };

        return results;
    }
}
