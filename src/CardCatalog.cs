using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;

namespace StS2Sim;

/// <summary>
/// The "what cards could I add?" list for the deck-editor UI: the character's
/// own card pool plus the colorless pool, with display metadata. Backed by
/// ModelDb, so it tracks game patches automatically. Requires
/// <see cref="Harness.Bootstrap"/> first.
/// </summary>
internal static class CardCatalog
{
    public sealed record Entry(string Id, string Name, string Cost, string Type, string Rarity);

    public static IReadOnlyList<Entry> GetAddableCards(string characterId)
    {
        var character = ModelDb.AllCharacters.FirstOrDefault(c => c.Id.ToString() == characterId);

        var pools = new List<CardPoolModel>();
        if (character != null) pools.Add(character.CardPool);
        pools.Add(ModelDb.CardPool<ColorlessCardPool>());

        var seen = new HashSet<string>();
        var list = new List<Entry>();
        foreach (var pool in pools)
        {
            foreach (var card in pool.AllCards)
            {
                var id = card.Id.ToString();
                if (!seen.Add(id)) continue;
                var cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.Canonical.ToString();
                list.Add(new Entry(id, CardLabels.PrettyName(id), cost, card.Type.ToString(), card.Rarity.ToString()));
            }
        }
        return list.OrderBy(e => e.Type).ThenBy(e => e.Name).ToList();
    }
}
