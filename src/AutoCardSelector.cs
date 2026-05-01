using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;

namespace StS2Sim;

/// <summary>
/// Card-select prompts (Armaments "pick a card to upgrade", Havoc "pick a card to play",
/// etc.) need a selector or they NRE. The game's normal selector talks to a UI;
/// for headless sims we just auto-pick the first <c>maxSelect</c> options
/// (or skip if <c>minSelect == 0</c>).
///
/// This is a "good enough" heuristic — it doesn't try to make optimal choices.
/// For decks with select-heavy cards we could swap in a smarter strategy later.
/// </summary>
internal sealed class AutoCardSelector : ICardSelector
{
    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
    {
        var list = options.ToList();
        var picked = list.Take(System.Math.Max(minSelect, System.Math.Min(maxSelect, list.Count)));
        return Task.FromResult<IEnumerable<CardModel>>(picked.ToList());
    }

    public CardModel? GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives)
        => options.FirstOrDefault()?.Card;
}
