using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim;

internal static class Sim
{
    public static async Task RunSmokeTest()
    {
        Harness.Bootstrap();
        await SmokeTests.RunAll();

        Console.WriteLine("=== StS2 Headless Sim — Damage-Per-Turn Benchmark ===\n");

        var decks = new List<(string name, List<Type> cards)>
        {
            ("Vanilla Ironclad starter (5 Strike, 4 Defend, 1 Bash)", new List<Type> {
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(Bash),
            }),
            ("All Strikes (10 Strike)", Enumerable.Repeat(typeof(StrikeIronclad), 10).Cast<Type>().ToList()),
            ("Strike + Bash heavy (5 Strike, 5 Bash)", new List<Type> {
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(Bash), typeof(Bash), typeof(Bash), typeof(Bash), typeof(Bash),
            }),
        };

        const int trials = 10_000;
        const int turns = 10;

        foreach (var (name, deck) in decks)
        {
            var sim = new DamagePerTurnSim
            {
                DeckName = name,
                Deck = deck,
                Turns = turns,
            };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var trialResults = await sim.RunTrials(trials);
            sw.Stop();
            ReportDeck(name, trialResults, turns);
            var totalTurnsRun = trials * turns;
            Console.WriteLine($"  Perf: {sw.ElapsedMilliseconds}ms total, {1000.0 * trials / sw.ElapsedMilliseconds:F0} trials/sec, {1000.0 * totalTurnsRun / sw.ElapsedMilliseconds:F0} turns/sec\n");
        }
    }

    private static void ReportDeck(string name, IReadOnlyList<DamagePerTurnSim.TrialResult> results, int turns)
    {
        var dpt = results.Select(r => r.AvgPerTurn).OrderBy(x => x).ToList();
        var totals = results.Select(r => r.TotalDamage).OrderBy(x => x).ToList();
        double mean = dpt.Average();
        double p50 = dpt[dpt.Count / 2];
        double p05 = dpt[(int)(dpt.Count * 0.05)];
        double p95 = dpt[(int)(dpt.Count * 0.95)];
        int totalMin = totals.First();
        int totalMax = totals.Last();
        double totalMean = totals.Average();

        Console.WriteLine($"Deck: {name}");
        Console.WriteLine($"  Trials: {results.Count}, Turns each: {turns}, Energy/turn: 3, Hand size: 5");
        Console.WriteLine($"  Damage/turn: mean={mean:F1}  p05={p05:F1}  p50={p50:F1}  p95={p95:F1}");
        Console.WriteLine($"  Total damage over {turns} turns: mean={totalMean:F0}  min={totalMin}  max={totalMax}");

        // Show one sample trial.
        var sample = results[0];
        Console.WriteLine($"  Sample trial (seed=0x{sample.Seed:X}):");
        foreach (var t in sample.Turns)
        {
            Console.WriteLine($"    Turn {t.Turn}: {t.Damage} dmg via [{string.Join(", ", t.CardsPlayed)}]");
        }
        Console.WriteLine();
    }
}
