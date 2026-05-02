using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// A policy decides which card from the player's hand to play next given the
/// current state — or returns null to end the turn.
/// </summary>
internal interface IPlayPolicy
{
    string Name { get; }
    /// <summary>Pick a card to play, or null to end turn.</summary>
    CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng);
}

/// <summary>
/// Shared filtering logic so all policies treat "this card is playable right now"
/// identically. Excludes cards with the Unplayable keyword (Ascenders Bane,
/// Burn, etc.) and cards we can't afford.
/// </summary>
internal static class Playable
{
    public static bool IsPlayable(CardModel card, int energyLeft)
    {
        if (card.Keywords.Contains(CardKeyword.Unplayable)) return false;
        if (card.EnergyCost.GetResolved() > energyLeft) return false;
        return true;
    }

    public static IEnumerable<CardModel> InHand(Harness.CombatHarness h, int energyLeft)
        => h.Player.PlayerCombatState!.Hand.Cards.Where(c => IsPlayable(c, energyLeft));
}
