using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// The planner brain's discriminating cases — each one is a turn the greedy
/// racer plays WRONG, asserted with the game's own numbers by running the
/// real play loop under <see cref="TurnPlanPolicy"/>.
/// </summary>
internal static class PlannerTests
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            // Energy knapsack: 2 energy, hand [Bash(2), Strike, Strike].
            // Greedy plays Bash for 8; two Strikes deal 12.
            await PlanCase(
                "Planner solves the knapsack: two Strikes (12) over Bash (8) at 2 energy",
                new List<Harness.DeckEntry> { new(typeof(Bash)), new(typeof(StrikeIronclad)), new(typeof(StrikeIronclad)) },
                energy: 2,
                expectDamage: 12),

            // Buff ordering: 3 energy, hand [Inflame, Strike, Strike].
            // Inflame first → (6+2)×2 = 16. Greedy never plays a 0-damage
            // card before attacks → 12 (+ Inflame after, wasted).
            await PlanCase(
                "Planner plays Inflame before Strikes (16 over 12)",
                new List<Harness.DeckEntry> { new(typeof(Inflame)), new(typeof(StrikeIronclad)), new(typeof(StrikeIronclad)) },
                energy: 3,
                expectDamage: 16),

            // Debuff ordering + knapsack together: 3 energy,
            // hand [Bash, Strike]. Bash first → 8 + floor(6×1.5)=9 → 17.
            await PlanCase(
                "Planner orders Bash before Strike for Vulnerable (17)",
                new List<Harness.DeckEntry> { new(typeof(Bash)), new(typeof(StrikeIronclad)) },
                energy: 3,
                expectDamage: 17),
        };
        return results;
    }

    /// <summary>Put the whole deck in hand, run the planner play loop at the
    /// given energy, assert total dummy damage.</summary>
    private static Task<TestHelpers.TestResult> PlanCase(
        string name, List<Harness.DeckEntry> deck, int energy, int expectDamage)
        => CharTestHelpers.Test<Ironclad>(name, deck, async h =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);
            foreach (var c in System.Linq.Enumerable.ToList(pcs.DrawPile.Cards))
            {
                pcs.DrawPile.RemoveInternal(c);
                pcs.Hand.AddInternal(c);
            }

            var before = h.Dummy.CurrentHp;
            await DamagePerTurnSim.RunPlayPhase(
                h, new TurnPlanPolicy(), new Random(1),
                chooseEnemyTarget: () => h.Dummy);
            return TestHelpers.Expect(before - h.Dummy.CurrentHp, expectDamage, "total damage");
        });
}
