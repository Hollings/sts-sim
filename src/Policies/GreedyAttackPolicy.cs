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
        foreach (var card in Playable.InHand(h, energyLeft))
        {
            if (card.Type != CardType.Attack) continue;
            return card;
        }
        return null;
    }
}
