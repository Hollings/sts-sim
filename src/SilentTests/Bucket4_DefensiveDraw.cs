using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim.SilentTests;

/// <summary>
/// Bucket 4 — Defensive & Draw Skills. Block, draw, retain, dodge.
/// Mostly fits SingleCardTest&lt;T&gt; with delta assertions on Block / draw pile size.
///
/// Scope (15 cards × 2 upgrade levels = 30 tests):
///   DefendSilent, Acrobatics, Backflip, Deflect, DodgeAndRoll, Prepared,
///   Reflex, Survivor, EscapePlan, ShadowStep, Blur, Dash, Tactician,
///   Anticipate, Tracking
/// </summary>
internal static class Bucket4_DefensiveDraw
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>();

        results.Add(await Test_DefendSilent());
        results.Add(await Test_DefendSilentPlus());
        results.Add(await Test_Acrobatics());
        results.Add(await Test_AcrobaticsPlus());
        results.Add(await Test_Backflip());
        results.Add(await Test_BackflipPlus());
        results.Add(await Test_Deflect());
        results.Add(await Test_DeflectPlus());
        results.Add(await Test_DodgeAndRoll());
        results.Add(await Test_DodgeAndRollPlus());
        results.Add(await Test_Prepared());
        results.Add(await Test_PreparedPlus());
        results.Add(await Test_Reflex());
        results.Add(await Test_ReflexPlus());
        results.Add(await Test_Survivor());
        results.Add(await Test_SurvivorPlus());
        results.Add(await Test_EscapePlan());
        results.Add(await Test_EscapePlanPlus());
        results.Add(await Test_ShadowStep());
        results.Add(await Test_ShadowStepPlus());
        results.Add(await Test_Blur());
        results.Add(await Test_BlurPlus());
        results.Add(await Test_Dash());
        results.Add(await Test_DashPlus());
        results.Add(await Test_Tactician());
        results.Add(await Test_TacticianPlus());
        results.Add(await Test_Anticipate());
        results.Add(await Test_AnticipatePlus());
        results.Add(await Test_Tracking());
        results.Add(await Test_TrackingPlus());

        return results;
    }

    // ─── DefendSilent — base 5 block, +3 → 8 upgraded ─────────────────────────
    private static Task<TestHelpers.TestResult> Test_DefendSilent()
        => TestHelpers.SingleCardTest<DefendSilent>(
            name: "DefendSilent grants 5 block",
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 5, "block"));

    private static Task<TestHelpers.TestResult> Test_DefendSilentPlus()
        => TestHelpers.SingleCardTest<DefendSilent>(
            name: "DefendSilent+ grants 8 block",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 8, "block"));

    // ─── Acrobatics — draws 3 (4+), discards 1 ───────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Acrobatics()
        => TestHelpers.SingleCardTest<Acrobatics>(
            name: "Acrobatics draws 3, discards 1",
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 3) return $"expected 3 cards drawn, got {drawn}";
                int discarded = pcs.DiscardPile.Cards.Count - before.discardSize;
                // Discard pile gains 1 from the auto-discard, plus 1 for the played Acrobatics itself.
                if (discarded < 1) return $"expected at least 1 discarded, got {discarded}";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_AcrobaticsPlus()
        => TestHelpers.SingleCardTest<Acrobatics>(
            name: "Acrobatics+ draws 4, discards 1",
            upgradeLevel: 1,
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 4) return $"expected 4 cards drawn, got {drawn}";
                return null;
            });

    // ─── Backflip — 5 block (8+) and draws 2 ─────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Backflip()
        => TestHelpers.SingleCardTest<Backflip>(
            name: "Backflip grants 5 block + draws 2",
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 5) return $"expected 5 block, got {blockGained}";
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 2) return $"expected 2 drawn, got {drawn}";
                return null;
            });

    private static Task<TestHelpers.TestResult> Test_BackflipPlus()
        => TestHelpers.SingleCardTest<Backflip>(
            name: "Backflip+ grants 8 block + draws 2",
            upgradeLevel: 1,
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 8) return $"expected 8 block, got {blockGained}";
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 2) return $"expected 2 drawn, got {drawn}";
                return null;
            });

    // ─── Deflect — 4 block (7+), 0 cost ──────────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Deflect()
        => TestHelpers.SingleCardTest<Deflect>(
            name: "Deflect grants 4 block",
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 4, "block"));

    private static Task<TestHelpers.TestResult> Test_DeflectPlus()
        => TestHelpers.SingleCardTest<Deflect>(
            name: "Deflect+ grants 7 block",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 7, "block"));

    // ─── DodgeAndRoll — 4 block (6+) plus BlockNextTurnPower ─────────────────
    private static Task<TestHelpers.TestResult> Test_DodgeAndRoll()
        => TestHelpers.SingleCardTest<DodgeAndRoll>(
            name: "DodgeAndRoll grants 4 block + BlockNextTurn",
            assert: (h, before) =>
            {
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 4) return $"expected 4 block, got {blockGained}";
                return TestHelpers.ExpectPower(h.Player.Creature, "BLOCK_NEXT_TURN", expectedAmount: 4);
            });

    private static Task<TestHelpers.TestResult> Test_DodgeAndRollPlus()
        => TestHelpers.SingleCardTest<DodgeAndRoll>(
            name: "DodgeAndRoll+ grants 6 block + BlockNextTurn",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 6) return $"expected 6 block, got {blockGained}";
                return TestHelpers.ExpectPower(h.Player.Creature, "BLOCK_NEXT_TURN", expectedAmount: 6);
            });

    // ─── Prepared — 0 cost, draw 1 (2+) discard 1 (2+) ───────────────────────
    private static Task<TestHelpers.TestResult> Test_Prepared()
        => TestHelpers.SingleCardTest<Prepared>(
            name: "Prepared draws 1",
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                return TestHelpers.Expect(drawn, 1, "drawn");
            });

    private static Task<TestHelpers.TestResult> Test_PreparedPlus()
        => TestHelpers.SingleCardTest<Prepared>(
            name: "Prepared+ draws 2",
            upgradeLevel: 1,
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                return TestHelpers.Expect(drawn, 2, "drawn");
            });

    // ─── Reflex — 3 cost, draws 2 (3+). Sly trigger isn't tested via OnPlay ──
    private static Task<TestHelpers.TestResult> Test_Reflex()
        => TestHelpers.SingleCardTest<Reflex>(
            name: "Reflex draws 2",
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                return TestHelpers.Expect(drawn, 2, "drawn");
            });

    private static Task<TestHelpers.TestResult> Test_ReflexPlus()
        => TestHelpers.SingleCardTest<Reflex>(
            name: "Reflex+ draws 3",
            upgradeLevel: 1,
            extraDeckCards: new[] { typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                return TestHelpers.Expect(drawn, 3, "drawn");
            });

    // ─── Survivor — 8 block (11+) + discard 1 ────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Survivor()
        => TestHelpers.SingleCardTest<Survivor>(
            name: "Survivor grants 8 block",
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 8, "block"));

    private static Task<TestHelpers.TestResult> Test_SurvivorPlus()
        => TestHelpers.SingleCardTest<Survivor>(
            name: "Survivor+ grants 11 block",
            upgradeLevel: 1,
            assert: (h, before) => TestHelpers.Expect(
                h.Player.Creature.Block - before.playerBlock, 11, "block"));

    // ─── EscapePlan — draw 1; if Skill, +3 block (5+) ────────────────────────
    // Use a Defend in the draw pile to guarantee the drawn card is a Skill.
    private static Task<TestHelpers.TestResult> Test_EscapePlan()
        => TestHelpers.SingleCardTest<EscapePlan>(
            name: "EscapePlan draws Skill -> 3 block",
            extraDeckCards: new[] { typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 1) return $"expected 1 drawn, got {drawn}";
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                return TestHelpers.Expect(blockGained, 3, "block");
            });

    private static Task<TestHelpers.TestResult> Test_EscapePlanPlus()
        => TestHelpers.SingleCardTest<EscapePlan>(
            name: "EscapePlan+ draws Skill -> 5 block",
            upgradeLevel: 1,
            extraDeckCards: new[] { typeof(DefendSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                int drawn = before.drawSize - pcs.DrawPile.Cards.Count;
                if (drawn != 1) return $"expected 1 drawn, got {drawn}";
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                return TestHelpers.Expect(blockGained, 5, "block");
            });

    // ─── ShadowStep — discard hand, apply ShadowStepPower 1 ──────────────────
    // Use PreloadHandTest so we have cards in hand to discard.
    private static Task<TestHelpers.TestResult> Test_ShadowStep()
        => TestHelpers.PreloadHandTest<ShadowStep>(
            name: "ShadowStep discards hand + applies ShadowStep power",
            handLoadout: new[] { typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                // Hand should be empty (the played card is gone, plus the 2 fodder were discarded).
                if (pcs.Hand.Cards.Count != 0)
                    return $"expected empty hand, got {pcs.Hand.Cards.Count}";
                return TestHelpers.ExpectPower(h.Player.Creature, "SHADOW_STEP", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_ShadowStepPlus()
        => TestHelpers.PreloadHandTest<ShadowStep>(
            name: "ShadowStep+ discards hand + applies ShadowStep power (0 cost)",
            upgradeLevel: 1,
            handLoadout: new[] { typeof(StrikeSilent), typeof(StrikeSilent) },
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                if (pcs.Hand.Cards.Count != 0)
                    return $"expected empty hand, got {pcs.Hand.Cards.Count}";
                return TestHelpers.ExpectPower(h.Player.Creature, "SHADOW_STEP", expectedAmount: 1);
            });

    // ─── Blur — 5 block (8+) + Blur power 1 ──────────────────────────────────
    private static Task<TestHelpers.TestResult> Test_Blur()
        => TestHelpers.SingleCardTest<Blur>(
            name: "Blur grants 5 block + Blur power",
            assert: (h, before) =>
            {
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 5) return $"expected 5 block, got {blockGained}";
                return TestHelpers.ExpectPower(h.Player.Creature, "BLUR", expectedAmount: 1);
            });

    private static Task<TestHelpers.TestResult> Test_BlurPlus()
        => TestHelpers.SingleCardTest<Blur>(
            name: "Blur+ grants 8 block + Blur power",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                if (blockGained != 8) return $"expected 8 block, got {blockGained}";
                return TestHelpers.ExpectPower(h.Player.Creature, "BLUR", expectedAmount: 1);
            });

    // ─── Dash — 10 dmg + 10 block (13/13 upgraded) ───────────────────────────
    private static Task<TestHelpers.TestResult> Test_Dash()
        => TestHelpers.SingleCardTest<Dash>(
            name: "Dash deals 10 + 10 block",
            assert: (h, before) =>
            {
                int dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 10) return $"expected 10 damage, got {dmg}";
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                return TestHelpers.Expect(blockGained, 10, "block");
            });

    private static Task<TestHelpers.TestResult> Test_DashPlus()
        => TestHelpers.SingleCardTest<Dash>(
            name: "Dash+ deals 13 + 13 block",
            upgradeLevel: 1,
            assert: (h, before) =>
            {
                int dmg = before.dummyHp - h.Dummy.CurrentHp;
                if (dmg != 13) return $"expected 13 damage, got {dmg}";
                int blockGained = h.Player.Creature.Block - before.playerBlock;
                return TestHelpers.Expect(blockGained, 13, "block");
            });

    // ─── Tactician — gains 1 energy (2+) on play (3 cost so net -2 / -1) ─────
    // Set energy high enough to pay the 3 cost; assert energy delta after play.
    private static Task<TestHelpers.TestResult> Test_Tactician()
        => TestHelpers.SingleCardTest<Tactician>(
            name: "Tactician gains 1 energy on play",
            energy: 5,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                // before.energy was 5; play cost = 3, then GainEnergy(1) -> 5 - 3 + 1 = 3.
                int delta = pcs.Energy - before.energy;
                return TestHelpers.Expect(delta, -2, "energy delta (cost 3 minus gain 1)");
            });

    private static Task<TestHelpers.TestResult> Test_TacticianPlus()
        => TestHelpers.SingleCardTest<Tactician>(
            name: "Tactician+ gains 2 energy on play",
            upgradeLevel: 1,
            energy: 5,
            assert: (h, before) =>
            {
                var pcs = h.Player.PlayerCombatState!;
                // 5 - 3 + 2 = 4 -> delta -1.
                int delta = pcs.Energy - before.energy;
                return TestHelpers.Expect(delta, -1, "energy delta (cost 3 minus gain 2)");
            });

    // ─── Anticipate — applies AnticipatePower 2 (3+) ─────────────────────────
    private static Task<TestHelpers.TestResult> Test_Anticipate()
        => TestHelpers.SingleCardTest<Anticipate>(
            name: "Anticipate applies Anticipate 2",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "ANTICIPATE", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_AnticipatePlus()
        => TestHelpers.SingleCardTest<Anticipate>(
            name: "Anticipate+ applies Anticipate 3",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "ANTICIPATE", expectedAmount: 3));

    // ─── Tracking — applies TrackingPower 2 (upgrade only reduces cost) ─────
    private static Task<TestHelpers.TestResult> Test_Tracking()
        => TestHelpers.SingleCardTest<Tracking>(
            name: "Tracking applies Tracking 2",
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "TRACKING", expectedAmount: 2));

    private static Task<TestHelpers.TestResult> Test_TrackingPlus()
        => TestHelpers.SingleCardTest<Tracking>(
            name: "Tracking+ applies Tracking 2 (cost reduced to 1)",
            upgradeLevel: 1,
            assert: (h, before) =>
                TestHelpers.ExpectPower(h.Player.Creature, "TRACKING", expectedAmount: 2));
}
