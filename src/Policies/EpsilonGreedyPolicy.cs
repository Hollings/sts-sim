using System;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// With probability epsilon, picks a random affordable card. Otherwise defers
/// to a base policy. This lets best-of-K sampling occasionally explore
/// non-greedy plays (e.g. "play Inflame turn 1 even though it deals 0 dmg")
/// while keeping the typical play sane. ε=0 collapses to base; ε=1 is uniform random.
///
/// CRUCIAL for deterministic bases (the planner): without ε, every one of a
/// seed's K samples replays the identical line, so best-of-K degenerates to
/// K=1 — the first bench of plain planner vs ε-race lost 60.1% → 52.9% for
/// exactly this reason. Wrapped, the planner both plans AND harvests K.
/// </summary>
internal sealed class EpsilonGreedyPolicy : IPlayPolicy, ITargetingPolicy
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
            var affordable = Playable.InHand(h, energyLeft).ToList();
            return Playable.ChooseFrom(affordable, cands => cands[rng.Next(cands.Count)]);
        }
        return _base.ChooseCard(h, energyLeft, rng);
    }

    /// <summary>Targeting passes through to the base policy (the play loop
    /// checks the OUTERMOST policy for <see cref="ITargetingPolicy"/>).</summary>
    public MegaCrit.Sts2.Core.Entities.Creatures.Creature? ChooseTarget(Harness.CombatHarness h, CardModel card)
        => (_base as ITargetingPolicy)?.ChooseTarget(h, card);
}
