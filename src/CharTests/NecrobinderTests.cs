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

            // Regression: user-reported crash adding Dredge in compare mode.
            // Dredge drives CardSelectCmd.FromSimpleGrid, which headless used
            // to fall into the multiplayer remote-choice wait (the selector
            // was only consulted when LocalContext.IsMe — never true here).
            // Now routed to AutoCardSelector: first 3 discard cards to hand.
            await CharTestHelpers.Test<Necrobinder>(
                "Dredge returns 3 cards from discard to hand",
                new List<Harness.DeckEntry>
                {
                    new(typeof(Dredge)), new(typeof(StrikeNecrobinder)), new(typeof(StrikeNecrobinder)),
                    new(typeof(StrikeNecrobinder)), new(typeof(DefendNecrobinder)),
                },
                async h =>
                {
                    var pcs = h.Player.PlayerCombatState!;
                    Reflect.SetEnergy(pcs, 9);
                    var dredge = CharTestHelpers.MoveToHand(h, typeof(Dredge))!;
                    foreach (var c in pcs.DrawPile.Cards.ToList())
                    {
                        pcs.DrawPile.RemoveInternal(c);
                        pcs.DiscardPile.AddInternal(c);
                    }
                    await CharTestHelpers.PlayCard(h, dredge);
                    return TestHelpers.Expect(pcs.Hand.Cards.Count, 3, "hand size after Dredge")
                        ?? TestHelpers.Expect(pcs.DiscardPile.Cards.Count, 1, "discard left")
                        ?? TestHelpers.Expect(pcs.ExhaustPile.Cards.OfType<Dredge>().Count(), 1, "Dredge exhausted");
                }),

            // ── Doom suite ────────────────────────────────────────────────
            // Doom executes its owner at the end of the OWNER's side's turn
            // when HP <= stacks. The enemy-side BeforeTurnEnd that triggers
            // it is the same TurnHooks call EncounterSim's EnemyTurn makes.

            await CharTestHelpers.Play1<Necrobinder, BlightStrike>(
                "Blight Strike deals 8 and dooms for damage dealt",
                (h, b) =>
                    TestHelpers.Expect(b.dummyHp - h.Dummy.CurrentHp, 8, "damage")
                    ?? TestHelpers.ExpectPower(h.Dummy, "DOOM", 8)),

            await CharTestHelpers.Test<Necrobinder>(
                "Doom executes at enemy turn end when HP <= stacks",
                new List<Harness.DeckEntry> { new(typeof(BlightStrike)) },
                async h =>
                {
                    Reflect.SetEnergy(h.Player.PlayerCombatState!, 9);
                    await CharTestHelpers.PlayCard(h, CharTestHelpers.MoveToHand(h, typeof(BlightStrike))!);
                    Reflect.SetCurrentHp(h.Dummy, 5); // inside the 8-stack threshold
                    await TurnHooks.FireBeforeTurnEnd(h, MegaCrit.Sts2.Core.Combat.CombatSide.Enemy);
                    return h.Dummy.IsAlive ? "dummy should be dead (HP 5 <= Doom 8)" : null;
                }),

            await CharTestHelpers.Test<Necrobinder>(
                "Doom holds while HP above stacks",
                new List<Harness.DeckEntry> { new(typeof(BlightStrike)) },
                async h =>
                {
                    Reflect.SetEnergy(h.Player.PlayerCombatState!, 9);
                    await CharTestHelpers.PlayCard(h, CharTestHelpers.MoveToHand(h, typeof(BlightStrike))!);
                    Reflect.SetCurrentHp(h.Dummy, 50);
                    await TurnHooks.FireBeforeTurnEnd(h, MegaCrit.Sts2.Core.Combat.CombatSide.Enemy);
                    return h.Dummy.IsAlive ? null : "dummy died with HP 50 > Doom 8";
                }),

            // Countdown ticks via AfterSideTurnStart: +N Doom to a random
            // enemy at every player turn start.
            await CharTestHelpers.Test<Necrobinder>(
                "Countdown applies Doom 6 at player turn start",
                new List<Harness.DeckEntry> { new(typeof(Countdown)) },
                async h =>
                {
                    Reflect.SetEnergy(h.Player.PlayerCombatState!, 9);
                    await CharTestHelpers.PlayCard(h, CharTestHelpers.MoveToHand(h, typeof(Countdown))!);
                    await TurnHooks.FireAfterSideTurnStart(h, MegaCrit.Sts2.Core.Combat.CombatSide.Player);
                    return TestHelpers.ExpectPower(h.Dummy, "DOOM", 6);
                }),

            // Doom-death reactions go through Hook.AfterDiedToDoom, which is
            // NetId-gated in the game — guarded here via the de-gating shim.
            await CharTestHelpers.Test<Necrobinder>(
                "Book Repair Knife heals 3 on a doom kill",
                new List<Harness.DeckEntry> { new(typeof(BlightStrike)) },
                async h =>
                {
                    Reflect.SetEnergy(h.Player.PlayerCombatState!, 9);
                    var knife = (MegaCrit.Sts2.Core.Models.RelicModel)
                        MegaCrit.Sts2.Core.Models.ModelDb.Relic<MegaCrit.Sts2.Core.Models.Relics.BookRepairKnife>().ToMutable();
                    h.Player.AddRelicInternal(knife, silent: true);
                    Reflect.SetCurrentHp(h.Player.Creature, h.Player.Creature.MaxHp - 10);
                    await CharTestHelpers.PlayCard(h, CharTestHelpers.MoveToHand(h, typeof(BlightStrike))!);
                    Reflect.SetCurrentHp(h.Dummy, 5);
                    var hpBefore = h.Player.Creature.CurrentHp;
                    await TurnHooks.FireBeforeTurnEnd(h, MegaCrit.Sts2.Core.Combat.CombatSide.Enemy);
                    if (h.Dummy.IsAlive) return "dummy should be dead";
                    return TestHelpers.Expect(h.Player.Creature.CurrentHp - hpBefore, 3, "knife heal");
                }),
        };

        return results;
    }
}
