using System;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Plays the affordable card with the highest "static damage"
/// (<c>card.DynamicVars.Damage.BaseValue</c>). Falls back to any affordable
/// card if none are direct attacks.
/// </summary>
internal sealed class HighestDamagePolicy : IPlayPolicy
{
    public string Name => "highest-damage";

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        var affordable = Playable.InHand(h, energyLeft).ToList();
        return Playable.ChooseFrom(affordable,
            cands => cands.OrderByDescending(EstimatedDamage).First());
    }

    internal static decimal EstimatedDamage(CardModel card)
    {
        if (card.Type != CardType.Attack) return 0m;
        try { return card.DynamicVars.Damage.BaseValue; }
        catch { return 0m; }
    }
}
