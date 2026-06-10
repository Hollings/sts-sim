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
/// identically, matching the game's own rules. Split in two tiers for speed:
/// a cheap screen (keyword + cost) applied to the whole hand, and the game's
/// full <c>CardModel.CanPlay()</c> — which iterates hook listeners and covers
/// card-logic gates (Clash's "only attacks in hand") and ShouldPlay preventers —
/// applied only to the would-be pick via <see cref="ChooseFrom"/>.
/// </summary>
internal static class Playable
{
    /// <summary>
    /// Cheap candidate screen: Unplayable keyword (Ascenders Bane, Burn) and
    /// energy budget. GetAmountToSpend (not GetResolved) so X-cost cards —
    /// which spend whatever you have, including 0 — always count as playable.
    /// </summary>
    public static bool PassesCheapFilter(CardModel card, int energyLeft)
        => !card.Keywords.Contains(CardKeyword.Unplayable)
           && card.EnergyCost.GetAmountToSpend() <= energyLeft;

    /// <summary>Full game-accurate check. Prefer ChooseFrom in hot paths.</summary>
    public static bool IsPlayable(CardModel card, int energyLeft)
        => PassesCheapFilter(card, energyLeft) && card.CanPlay();

    /// <summary>
    /// Cheap-filtered hand. Callers must confirm their final candidate with
    /// CanPlay() — most simply by selecting through <see cref="ChooseFrom"/>.
    /// </summary>
    public static IEnumerable<CardModel> InHand(Harness.CombatHarness h, int energyLeft)
        => h.Player.PlayerCombatState!.Hand.Cards.Where(c => PassesCheapFilter(c, energyLeft));

    /// <summary>
    /// Run <paramref name="selector"/> over the candidates and validate the
    /// pick with the game's CanPlay(). If a hook vetoes it (rare), drop it and
    /// re-select, so policies never end the turn while legal plays remain.
    /// Mutates <paramref name="candidates"/> on retry.
    /// </summary>
    public static CardModel? ChooseFrom(List<CardModel> candidates, Func<List<CardModel>, CardModel?> selector)
    {
        while (candidates.Count > 0)
        {
            var pick = selector(candidates);
            if (pick == null) return null;
            if (pick.CanPlay()) return pick;
            candidates.Remove(pick);
        }
        return null;
    }
}
