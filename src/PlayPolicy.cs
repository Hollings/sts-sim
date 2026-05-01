using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// A policy decides which card from the player's hand to play next given
/// the current state — or returns null to end the turn.
/// </summary>
internal interface IPlayPolicy
{
    string Name { get; }
    /// <summary>Pick a card to play, or null to end turn.</summary>
    CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng);
}

/// <summary>Plays attack cards left-to-right; ends turn when none affordable.</summary>
internal sealed class GreedyAttackPolicy : IPlayPolicy
{
    public string Name => "greedy-attack";

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        foreach (var card in h.Player.PlayerCombatState!.Hand.Cards)
        {
            if (card.Type != CardType.Attack) continue;
            if (card.EnergyCost.GetResolved() > energyLeft) continue;
            return card;
        }
        return null;
    }
}

/// <summary>Plays any affordable card uniformly at random; ends turn 10% of the time even if cards remain.</summary>
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

        var affordable = h.Player.PlayerCombatState!.Hand.Cards
            .Where(c => c.EnergyCost.GetResolved() <= energyLeft)
            .ToList();
        if (affordable.Count == 0) return null;
        return affordable[rng.Next(affordable.Count)];
    }
}

/// <summary>
/// Plays the affordable card with the highest "static damage" (card.DamageVar.BaseValue × hits).
/// Falls back to any affordable card if none are direct attacks.
/// </summary>
internal sealed class HighestDamagePolicy : IPlayPolicy
{
    public string Name => "highest-damage";

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        var affordable = h.Player.PlayerCombatState!.Hand.Cards
            .Where(c => c.EnergyCost.GetResolved() <= energyLeft)
            .ToList();
        if (affordable.Count == 0) return null;

        return affordable
            .OrderByDescending(EstimatedDamage)
            .First();
    }

    private static decimal EstimatedDamage(CardModel card)
    {
        if (card.Type != CardType.Attack) return 0m;
        try { return card.DynamicVars.Damage.BaseValue; }
        catch { return 0m; }
    }
}

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
