using System;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Plays any affordable card uniformly at random; optionally ends turn early
/// (with probability <c>earlyEndProbability</c>) even if cards remain.
/// </summary>
internal sealed class RandomPolicy : IPlayPolicy
{
    private readonly double _earlyEndProbability;
    public string Name => "random";

    public RandomPolicy(double earlyEndProbability = 0.0)
    {
        _earlyEndProbability = earlyEndProbability;
    }

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        if (_earlyEndProbability > 0 && rng.NextDouble() < _earlyEndProbability) return null;

        var affordable = Playable.InHand(h, energyLeft).ToList();
        return Playable.ChooseFrom(affordable, cands => cands[rng.Next(cands.Count)]);
    }
}
