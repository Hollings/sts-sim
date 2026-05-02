using System;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

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
