using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// The planner brain's discriminating cases — turns the greedy racer plays
/// wrong, asserted with the game's own numbers by running the real play loop
/// under <see cref="TurnPlanPolicy"/>. The 2-energy [Bash, Strike, Strike]
/// hand appears twice on purpose: against a nearly-dead enemy the right play
/// is two Strikes (kill now), against a healthy one it's Bash (bank
/// Vulnerable for future turns). The planner prices that tradeoff instead of
/// following either as a rule.
/// </summary>
internal static class PlannerTests
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            // Kill-now: enemy at 12 HP, 2 energy. Two Strikes kill (12
            // effective); Bash would deal 8 and bank Vulnerable a corpse
            // can't pay out. Greedy plays Bash here too — wrong twice over.
            await PlanCase(
                "Planner kills now: two Strikes finish a 12 HP enemy over Bash",
                new List<Harness.DeckEntry> { new(typeof(Bash)), new(typeof(StrikeIronclad)), new(typeof(StrikeIronclad)) },
                energy: 2,
                dummyHp: 12,
                expectDamage: 12,
                expectDummyDead: true),

            // Setup: same hand, same energy, healthy enemy. Bash's 8 now plus
            // Vulnerable on every future turn beats 12 once.
            await PlanCase(
                "Planner banks Vulnerable vs a healthy enemy: Bash over two Strikes",
                new List<Harness.DeckEntry> { new(typeof(Bash)), new(typeof(StrikeIronclad)), new(typeof(StrikeIronclad)) },
                energy: 2,
                dummyHp: null,
                expectDamage: 8,
                expectVulnerable: true),

            // Buff ordering: 3 energy, hand [Inflame, Strike, Strike].
            // Inflame first → (6+2)×2 = 16. Greedy never plays a 0-damage
            // card before attacks → 12.
            await PlanCase(
                "Planner plays Inflame before Strikes (16 over 12)",
                new List<Harness.DeckEntry> { new(typeof(Inflame)), new(typeof(StrikeIronclad)), new(typeof(StrikeIronclad)) },
                energy: 3,
                dummyHp: null,
                expectDamage: 16),

            // Debuff ordering: 3 energy, hand [Bash, Strike].
            // Bash first → 8 + floor(6×1.5)=9 → 17.
            await PlanCase(
                "Planner orders Bash before Strike for Vulnerable (17)",
                new List<Harness.DeckEntry> { new(typeof(Bash)), new(typeof(StrikeIronclad)) },
                energy: 3,
                dummyHp: null,
                expectDamage: 17),
        };
        return results;
    }

    /// <summary>Put the whole deck in hand, optionally set the dummy's HP,
    /// run the planner play loop at the given energy, assert outcomes.</summary>
    private static Task<TestHelpers.TestResult> PlanCase(
        string name, List<Harness.DeckEntry> deck, int energy, int? dummyHp,
        int expectDamage, bool expectDummyDead = false, bool expectVulnerable = false)
        => CharTestHelpers.Test<Ironclad>(name, deck, async h =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);
            if (dummyHp.HasValue) Reflect.SetCurrentHp(h.Dummy, dummyHp.Value);
            foreach (var c in System.Linq.Enumerable.ToList(pcs.DrawPile.Cards))
            {
                pcs.DrawPile.RemoveInternal(c);
                pcs.Hand.AddInternal(c);
            }

            var before = h.Dummy.CurrentHp;
            await DamagePerTurnSim.RunPlayPhase(
                h, new TurnPlanPolicy(), new Random(1),
                chooseEnemyTarget: () => h.Dummy.IsAlive ? h.Dummy : null);

            var f = TestHelpers.Expect(before - h.Dummy.CurrentHp, expectDamage, "total damage");
            if (f != null) return f;
            if (expectDummyDead && h.Dummy.IsAlive) return "expected the dummy dead";
            if (expectVulnerable)
            {
                var p = TestHelpers.ExpectPower(h.Dummy, "VULNERABLE");
                if (p != null) return p;
            }
            return null;
        });
}
