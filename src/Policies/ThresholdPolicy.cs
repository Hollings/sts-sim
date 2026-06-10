using System;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// "HP is a resource; death is the only real cost." Plays pure
/// highest-damage race — until the telegraphed incoming damage would drop
/// the player below a respect threshold, then switches to stacking block
/// until the hit is covered (or no block cards remain).
///
/// The threshold is a fraction of max HP:
///   &lt; 0   → never defend (the old pure-race behavior)
///   0.15  → tank everything except near-lethal hits
///   ∞     → block whenever the intent out-damages current block (turtle)
///
/// One policy per personality; <see cref="BestOfKRunner"/> samples a
/// portfolio of them per seed and lets max-over-outcomes arbitrate whether
/// tanking the hit was worth it — no hand-tuned EV judgment anywhere.
/// </summary>
internal sealed class ThresholdPolicy : IPlayPolicy
{
    private readonly double _respectFraction;
    public string Name { get; }

    public ThresholdPolicy(double respectFraction)
    {
        _respectFraction = respectFraction;
        Name = respectFraction < 0 ? "race"
             : double.IsPositiveInfinity(respectFraction) ? "turtle"
             : $"thr{(int)Math.Round(respectFraction * 100)}";
    }

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        var affordable = Playable.InHand(h, energyLeft).ToList();
        if (affordable.Count == 0) return null;

        if (ShouldDefend(h))
        {
            var blockers = affordable.Where(c => EstimatedBlock(c) > 0).ToList();
            var pick = Playable.ChooseFrom(blockers,
                cands => cands.OrderByDescending(EstimatedBlock).First());
            if (pick != null) return pick;
            // No block left in hand — fall through and keep swinging.
        }

        return Playable.ChooseFrom(affordable,
            cands => cands.OrderByDescending(HighestDamagePolicy.EstimatedDamage).First());
    }

    /// <summary>
    /// Re-evaluated on every pick, so the policy stops blocking mid-turn the
    /// moment block covers the telegraph and spends the rest on damage.
    /// </summary>
    private bool ShouldDefend(Harness.CombatHarness h)
    {
        if (_respectFraction < 0) return false;

        var incoming = IntentReader.IncomingDamage(h);
        if (incoming <= 0) return false;

        var creature = h.Player.Creature;
        var unblocked = incoming - creature.Block;
        if (unblocked <= 0) return false;

        if (double.IsPositiveInfinity(_respectFraction)) return true;
        return creature.CurrentHp - unblocked <= creature.MaxHp * _respectFraction;
    }

    private static decimal EstimatedBlock(CardModel card)
    {
        try { return card.DynamicVars.Block.BaseValue; }
        catch { return 0m; }
    }
}
