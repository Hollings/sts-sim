using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Necrobinder battery. Centered on Osty — the pet creature that lives on the
/// ally side of the combat state and that half the card pool keys off
/// (FromOsty attacks, missing-Osty whiffs, IsPlayable gates, kill-for-block).
/// Also covers Soul token generation, the combat-history-driven Rattle, and the
/// starter relic's combat-start summon (which needs TurnHooks.FireBeforeCombatStart).
/// </summary>
internal static class NecrobinderTests
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            await CharTestHelpers.Play1<Necrobinder, StrikeNecrobinder>(
                "StrikeNecrobinder deals 6",
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 6, "damage")),

            // Starter relic summons the initial 1-HP Osty at combat start —
            // guards the FireBeforeCombatStart wiring.
            await CharTestHelpers.Test<Necrobinder>(
                "Bound Phylactery summons 1-HP Osty at combat start",
                new List<Harness.DeckEntry> { new(typeof(StrikeNecrobinder)) },
                async h =>
                {
                    await TurnHooks.FireBeforeCombatStart(h);
                    var osty = h.Player.Osty;
                    if (osty == null || !osty.IsAlive) return "Osty missing after combat start";
                    return TestHelpers.Expect(osty.MaxHp, 1, "Osty max HP")
                        ?? TestHelpers.ExpectPower(osty, "DIE_FOR_YOU");
                }),

            await CharTestHelpers.Play1<Necrobinder, Bodyguard>(
                "Bodyguard summons a 5-HP Osty",
                (h, b) =>
                {
                    var osty = h.Player.Osty;
                    if (osty == null || !osty.IsAlive) return "Osty missing after Bodyguard";
                    return TestHelpers.Expect(osty.MaxHp, 5, "Osty max HP")
                        ?? TestHelpers.Expect(osty.CurrentHp, 5, "Osty current HP");
                }),

            // Summoning while Osty lives adds max HP instead of replacing.
            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Bodyguard twice stacks Osty to 10 max HP",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(Bodyguard)) },
                (h, b) => TestHelpers.Expect(h.Player.Osty?.MaxHp ?? 0, 10, "Osty max HP")),

            // Unleash: 6 base + 1 per Osty HP.
            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Unleash with 5-HP Osty deals 11",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(Unleash)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 11, "damage")),

            // Missing-Osty whiff goes through the ShakeOstyIfDead UI shim.
            await CharTestHelpers.Play1<Necrobinder, Unleash>(
                "Unleash without Osty deals nothing",
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 0, "damage")),

            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Poke with Osty deals 6 (Osty attack)",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(Poke)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 6, "damage")),

            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Sic 'Em deals 5 and applies SicEmPower 2",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(SicEm)) },
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 5, "damage")
                    ?? TestHelpers.ExpectPower(h.Dummy, "SIC_EM", 2)),

            // IsPlayable gate: HighFive is unplayable while Osty is missing.
            await CharTestHelpers.Test<Necrobinder>(
                "High Five unplayable without Osty",
                new List<Harness.DeckEntry> { new(typeof(HighFive)) },
                h =>
                {
                    Reflect.SetEnergy(h.Player.PlayerCombatState!, 9);
                    var card = CharTestHelpers.MoveToHand(h, typeof(HighFive))!;
                    return Task.FromResult(card.CanPlay()
                        ? "expected CanPlay() == false with no Osty"
                        : (string?)null);
                }),

            await CharTestHelpers.PlaySeq<Necrobinder>(
                "High Five with Osty deals 11 and applies Vulnerable 2",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(HighFive)) },
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 11, "damage")
                    ?? TestHelpers.ExpectPower(h.Dummy, "VULNERABLE", 2)),

            // Generated-token pipeline: unblockable chip + 3 Souls into draw.
            await CharTestHelpers.Play1<Necrobinder, CaptureSpirit>(
                "Capture Spirit chips 3 and shuffles 3 Souls into draw",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 3, "damage")
                    ?? TestHelpers.Expect(
                        h.Player.PlayerCombatState!.DrawPile.Cards.OfType<Soul>().Count(), 3, "Souls in draw"),
                extraDeckCards: new[] { typeof(StrikeNecrobinder), typeof(StrikeNecrobinder) }),

            // Soul token: 0-cost, draw 2, exhausts itself.
            await CharTestHelpers.Test<Necrobinder>(
                "Soul draws 2 and exhausts",
                new List<Harness.DeckEntry>
                {
                    new(typeof(StrikeNecrobinder)), new(typeof(StrikeNecrobinder)),
                    new(typeof(StrikeNecrobinder)), new(typeof(DefendNecrobinder)),
                },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    var soul = (await Soul.CreateInHand(h.Player, 1, h.State)).Single();
                    var drawBefore = pcs.DrawPile.Cards.Count;
                    await CharTestHelpers.PlayCard(h, soul);
                    return TestHelpers.Expect(drawBefore - pcs.DrawPile.Cards.Count, 2, "cards drawn")
                        ?? TestHelpers.Expect(pcs.ExhaustPile.Cards.Count, 1, "exhaust pile");
                }),

            // Kill-your-pet-for-block. Block = Osty max HP × 2.
            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Sacrifice kills Osty for 10 block",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(Sacrifice)) },
                (h, b) =>
                    TestHelpers.Expect(h.Player.Creature.Block - b.playerBlock, 10, "block")
                    ?? (h.Player.IsOstyAlive ? "expected Osty dead after Sacrifice" : null)),

            // Rattle reads CombatHistory: hits = 1 + Osty attacks this turn.
            await CharTestHelpers.PlaySeq<Necrobinder>(
                "Rattle hits twice after one Osty attack (7x2 = 14)",
                new List<Harness.DeckEntry> { new(typeof(Bodyguard)), new(typeof(Poke)), new(typeof(Rattle)) },
                (h, b) => TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 6 + 14, "total damage")),
        };

        return results;
    }
}
