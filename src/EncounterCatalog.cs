using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Acts;
using MegaCrit.Sts2.Core.Rooms;

namespace StS2Sim;

/// <summary>
/// The "which fight?" list for the opponent picker: every real combat
/// encounter (normal / elite / boss) grouped by act, with display names.
/// Backed by ModelDb so it tracks game patches. Requires
/// <see cref="Harness.Bootstrap"/> first.
/// </summary>
internal static class EncounterCatalog
{
    public sealed record Entry(string Id, string Name, string RoomType, string Act);

    public static IReadOnlyList<Entry> GetEncounters()
    {
        var seen = new HashSet<string>();
        var list = new List<Entry>();
        foreach (var act in ActModel.GetDefaultList())
        {
            var actName = CardLabels.PrettyName(act.Id.ToString());
            foreach (var enc in act.AllEncounters)
            {
                if (enc.IsDebugEncounter) continue;
                if (enc.RoomType is not (RoomType.Monster or RoomType.Elite or RoomType.Boss)) continue;
                var id = enc.Id.ToString();
                if (!seen.Add(id)) continue;
                list.Add(new Entry(id, CardLabels.PrettyName(id), enc.RoomType.ToString(), actName));
            }
        }
        // Bosses first within each act — they're the headline use case.
        return list
            .OrderBy(e => e.Act)
            .ThenBy(e => e.RoomType switch { "Boss" => 0, "Elite" => 1, _ => 2 })
            .ThenBy(e => e.Name)
            .ToList();
    }
}
