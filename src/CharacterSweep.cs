using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Coverage battery for characters: for every character in ModelDb, run one
/// dummy-mode trial and one real-encounter trial with the character's actual
/// starter deck. A crash means character-specific mechanics (orbs, summons,
/// stars, ...) hit something unshimmed. Invoke:
/// <c>dotnet run -c Release -- character-sweep</c>.
/// </summary>
internal static class CharacterSweep
{
    public static async Task<int> RunAll()
    {
        Console.WriteLine("=== StS2 Character Sweep — starter deck, dummy + real fight ===\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var fightId = EncounterCatalog.GetEncounters()
            .FirstOrDefault(e => e.Id.Contains("SLIMES_WEAK"))?.Id
            ?? EncounterCatalog.GetEncounters().First(e => e.RoomType == "Monster").Id;

        int crashes = 0;
        foreach (var character in ModelDb.AllCharacters)
        {
            var id = character.Id.ToString();
            var charType = character.GetType();
            string dummyResult;
            string fightResult;

            try
            {
                var sim = new DamagePerTurnSim
                {
                    DeckName = id,
                    Deck = null, // real starter deck
                    CharacterType = charType,
                    Turns = 3,
                    Policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.3),
                };
                var r = await sim.RunSingleTrial(0xFACE);
                dummyResult = $"dmg={r.TotalDamage}/3t";
            }
            catch (Exception ex)
            {
                dummyResult = "CRASH: " + CrashDetail(ex);
                crashes++;
            }

            try
            {
                var sim = new EncounterSim
                {
                    Deck = null, // real starter deck
                    EncounterId = fightId,
                    CharacterType = charType,
                    MaxTurns = 8,
                    Policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.3),
                };
                var r = await sim.RunSingleTrial(0xFACE);
                fightResult = $"{(r.Win ? "WIN" : "loss")} in {r.TurnsTaken}t, hp {r.PlayerHpRemaining}/{r.PlayerMaxHp}";
            }
            catch (Exception ex)
            {
                fightResult = "CRASH: " + CrashDetail(ex);
                crashes++;
            }

            var status = dummyResult.StartsWith("CRASH") || fightResult.StartsWith("CRASH") ? "CRASH" : "PASS ";
            Console.WriteLine($"  [{status}] {CardLabels.PrettyName(id),-22} dummy: {dummyResult,-44} fight: {fightResult}");
        }

        sw.Stop();
        Console.WriteLine($"\n  {(crashes == 0 ? "All characters pass." : $"{crashes} crash(es).")} Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        return crashes > 0 ? 2 : 0;
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOf('\n');
        return idx >= 0 ? s[..idx].Trim() : s;
    }

    private static string CrashDetail(Exception ex)
    {
        var frames = (ex.StackTrace ?? "").Split('\n')
            .Where(f => f.Contains("MegaCrit") || f.Contains("StS2Sim"))
            .Take(3).Select(f => f.Trim());
        return $"{ex.GetType().Name}: {FirstLine(ex.Message)}\n      {string.Join("\n      ", frames)}";
    }
}

