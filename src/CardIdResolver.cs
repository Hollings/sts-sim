using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Maps save-file card IDs ("CARD.STRIKE_IRONCLAD") to C# Types
/// (typeof(StrikeIronclad)). Backed by ModelDb, so it covers every card
/// in the game. Requires Harness.Bootstrap() to have been called first.
/// </summary>
internal static class CardIdResolver
{
    /// <summary>"CARD.STRIKE_IRONCLAD" → typeof(StrikeIronclad). Returns null if unknown.</summary>
    public static Type? Resolve(string cardId)
    {
        var modelId = ModelId.Deserialize(cardId);
        var card = ModelDb.GetByIdOrNull<MegaCrit.Sts2.Core.Models.CardModel>(modelId);
        return card?.GetType();
    }

    /// <summary>Resolve a list of card IDs, throwing on first miss.</summary>
    public static IReadOnlyList<Type> ResolveAll(IEnumerable<string> cardIds)
    {
        var result = new List<Type>();
        foreach (var id in cardIds)
        {
            var t = Resolve(id);
            if (t == null)
                throw new ArgumentException($"Unknown card id '{id}'. Is ModelDb initialized?");
            result.Add(t);
        }
        return result;
    }

    /// <summary>"CARD.STRIKE_IRONCLAD" → "Strike Ironclad" for display.</summary>
    public static string PrettyName(string cardId)
    {
        var parts = cardId.Split('.');
        if (parts.Length < 2) return cardId;
        var entry = parts[^1].Replace('_', ' ').ToLowerInvariant();
        return string.Concat(entry.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]).Select(w => w + ' ')).TrimEnd();
    }

    /// <summary>List every CardModel id in the game (for the "swap a card" picker).</summary>
    public static IEnumerable<string> AllCardIds()
        => ModelDb.AllCards.Select(c => c.Id.ToString());
}
