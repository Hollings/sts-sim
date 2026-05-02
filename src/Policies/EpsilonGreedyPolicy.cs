using System;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// With probability epsilon, picks a random affordable card. Otherwise defers
/// to a base policy. This lets best-of-K sampling occasionally explore
/// non-greedy plays (e.g. "play Inflame turn 1 even though it deals 0 dmg")
/// while keeping the typical play sane. ε=0 collapses to base; ε=1 is uniform random.
/// </summary>
internal sealed class EpsilonGreedyPolicy : IPlayPolicy
{
    private readonly IPlayPolicy _base;
    private readonly double _epsilon;
    public string Name { get; }

    public EpsilonGreedyPolicy(IPlayPolicy basePolicy, double epsilon)
    {
        _base = basePolicy;
        _epsilon = epsilon;
        Name = $"eps{epsilon:F2}-{basePolicy.Name}";
    }

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        if (rng.NextDouble() < _epsilon)
        {
            var affordable = h.Player.PlayerCombatState!.Hand.Cards
                .Where(c => c.EnergyCost.GetResolved() <= energyLeft)
                .ToList();
            if (affordable.Count == 0) return null;
            return affordable[rng.Next(affordable.Count)];
        }
        return _base.ChooseCard(h, energyLeft, rng);
    }
}
