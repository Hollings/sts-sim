using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Display formatting for card identifiers. Single source of truth for
/// "CARD.STRIKE_IRONCLAD" → "Strike Ironclad" and the "+N" upgrade suffix.
/// </summary>
internal static class CardLabels
{
    /// <summary>"CARD.STRIKE_IRONCLAD" → "Strike Ironclad". Returns input unchanged if it has no '.'.</summary>
    public static string PrettyName(string id)
    {
        var parts = id.Split('.');
        if (parts.Length < 2) return id;
        var entry = parts[^1].Replace('_', ' ').ToLowerInvariant();
        return string.Concat(entry.Split(' ').Select(w => char.ToUpperInvariant(w[0]) + w[1..]).Select(w => w + ' ')).TrimEnd();
    }

    /// <summary>Pretty name + "+" / "+N" suffix when upgraded. Level 1 prints as a bare "+".</summary>
    public static string UpgradeSuffix(int upgradeLevel)
        => upgradeLevel <= 0 ? "" : upgradeLevel == 1 ? "+" : "+" + upgradeLevel;

    /// <summary>"CARD.STRIKE_IRONCLAD" + level 2 → "Strike Ironclad+2".</summary>
    public static string Format(string id, int upgradeLevel)
        => PrettyName(id) + UpgradeSuffix(upgradeLevel);

    /// <summary>Format a live CardModel using its current upgrade state.</summary>
    public static string Format(CardModel card)
        => Format(card.Id.ToString(), card.IsUpgraded ? card.CurrentUpgradeLevel : 0);
}
