using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim;

/// <summary>
/// Coverage battery for encounter mode: run one short trial against every
/// encounter in the catalog and report pass / crash per encounter. A crash
/// means the harness is missing a shim (some monster move touches a UI
/// singleton we haven't stubbed) — exactly the kind of thing to fix before
/// trusting boss verdicts. Invoke: <c>dotnet run -c Release -- encounter-sweep</c>.
/// </summary>
internal static class EncounterSweep
{
    public static async Task<int> RunAll(string? filter = null)
    {
        Console.WriteLine("=== StS2 Encounter Sweep — one trial vs every encounter ===\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var deck = Harness.AsEntries(new List<Type>
        {
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(Bash),
        });

        var results = new List<(EncounterCatalog.Entry Enc, string Status, string Detail)>();
        var encounters = EncounterCatalog.GetEncounters()
            .Where(e => filter == null || e.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var enc in encounters)
        {
            try
            {
                var sim = new EncounterSim
                {
                    Deck = deck,
                    EncounterId = enc.Id,
                    MaxTurns = 10,
                    Policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.3),
                };
                var r = await sim.RunSingleTrial(0xBEEF);
                var detail = $"{(r.Win ? "WIN" : "loss")} in {r.TurnsTaken}t · player {r.PlayerHpRemaining}/{r.PlayerMaxHp} · enemy {r.EnemyHpRemaining} hp left";
                // A fight where neither side ever took damage means the enemy
                // turns aren't really running — flag it, don't call it a pass.
                bool somethingHappened = r.PlayerHpRemaining < r.PlayerMaxHp || r.Win || r.Turns.Any(t => t.Damage > 0);
                results.Add((enc, somethingHappened ? "PASS" : "INERT", detail));
            }
            catch (Exception ex)
            {
                // Filtered (focused) runs print the whole exception — that's
                // what you're here for; the full sweep keeps one line each.
                if (filter != null)
                    results.Add((enc, "CRASH", "\n" + ex));
                else
                {
                    var frame = (ex.StackTrace ?? "").Split('\n').FirstOrDefault()?.Trim() ?? "";
                    results.Add((enc, "CRASH", $"{ex.GetType().Name}: {ex.Message} {frame}"));
                }
            }
        }
        sw.Stop();

        foreach (var (enc, status, detail) in results)
            Console.WriteLine($"  [{status,-5}] {enc.Act,-22} {enc.RoomType,-7} {enc.Name,-32} {detail}");

        int pass = results.Count(r => r.Status == "PASS");
        int inert = results.Count(r => r.Status == "INERT");
        int crash = results.Count(r => r.Status == "CRASH");
        Console.WriteLine($"\n  TOTAL: {pass}/{results.Count} pass · {inert} inert · {crash} crash · {sw.Elapsed.TotalSeconds:F1}s");
        return crash > 0 ? 2 : 0;
    }
}
