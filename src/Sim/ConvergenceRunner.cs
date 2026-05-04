using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// Console-only. Runs a deck under a play policy, tracking best-so-far and
/// running average across many trials. Prints periodic progress and dumps a
/// per-trial CSV you can plot to inspect convergence behavior (does best
/// plateau quickly? is the gap between policies meaningful or noise?).
///
/// Not used by the web UI — kept for ad-hoc debugging via experiment mode.
/// </summary>
internal sealed class ConvergenceRunner
{
    public required string DeckName { get; init; }
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public required IPlayPolicy Policy { get; init; }
    public int Turns { get; init; } = 5;
    public int HandSize { get; init; } = 5;
    public TimeSpan? TimeBudget { get; init; }
    public int? TrialBudget { get; init; }
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(1);
    public string? CsvPath { get; init; }

    public async Task<Summary> Run()
    {
        var sim = new DamagePerTurnSim
        {
            DeckName = DeckName,
            Deck = Deck,
            Turns = Turns,
            HandSize = HandSize,
            Policy = Policy,
        };

        var sw = Stopwatch.StartNew();
        long total = 0;
        int best = int.MinValue;
        int trial = 0;
        int lastPrintTrial = 0;
        var lastPrint = TimeSpan.Zero;
        DamagePerTurnSim.TrialResult? bestTrial = null;
        StreamWriter? csv = null;

        if (CsvPath != null)
        {
            csv = new StreamWriter(CsvPath);
            csv.WriteLine("trial,seed,damage,best_so_far,avg_so_far");
        }

        Console.WriteLine($"\n=== Anytime run: {DeckName} | policy={Policy.Name} | budget={(TimeBudget?.ToString() ?? $"{TrialBudget} trials")} ===");

        while (true)
        {
            if (TimeBudget != null && sw.Elapsed >= TimeBudget) break;
            if (TrialBudget != null && trial >= TrialBudget) break;

            // Use trial# as RNG seed input so different trials → different shuffles + different policy choices.
            // Mix in time so re-running gives different paths (helps surface variance).
            var trialSeed = unchecked((uint)(trial * 0x9E3779B1u + 0xC0FFEEu));
            var policyRngSeed = unchecked((uint)(trial * 0x85EBCA77u + 0xBADCAFEu));

            var simInst = new DamagePerTurnSim
            {
                DeckName = sim.DeckName,
                Deck = sim.Deck,
                Turns = sim.Turns,
                HandSize = sim.HandSize,
                Policy = sim.Policy,
                PolicyRngSeed = policyRngSeed,
            };
            var result = await simInst.RunSingleTrial(trialSeed);

            trial++;
            total += result.TotalDamage;
            if (result.TotalDamage > best)
            {
                best = result.TotalDamage;
                bestTrial = result;
            }
            csv?.WriteLine($"{trial},{trialSeed:X},{result.TotalDamage},{best},{(double)total / trial:F2}");

            if (sw.Elapsed - lastPrint >= ProgressInterval)
            {
                var deltaTrials = trial - lastPrintTrial;
                var deltaTime = sw.Elapsed - lastPrint;
                var rate = deltaTrials / deltaTime.TotalSeconds;
                Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F1}s  trials={trial}  best={best}  avg={(double)total / trial:F1}  rate={rate:F0}/s");
                lastPrint = sw.Elapsed;
                lastPrintTrial = trial;
            }
        }

        csv?.Dispose();
        sw.Stop();

        var summary = new Summary
        {
            DeckName = DeckName,
            PolicyName = Policy.Name,
            Trials = trial,
            BestDamage = best,
            AverageDamage = (double)total / Math.Max(trial, 1),
            Elapsed = sw.Elapsed,
            BestTrial = bestTrial,
        };

        Console.WriteLine();
        Console.WriteLine($"  Done. {trial} trials in {sw.Elapsed.TotalSeconds:F1}s ({trial / sw.Elapsed.TotalSeconds:F0}/s)");
        Console.WriteLine($"  Best:    {summary.BestDamage} dmg over {Turns} turns ({(double)summary.BestDamage / Turns:F2}/turn)");
        Console.WriteLine($"  Average: {summary.AverageDamage:F1} dmg ({summary.AverageDamage / Turns:F2}/turn)");
        if (bestTrial != null)
        {
            Console.WriteLine($"  Best trial breakdown (seed=0x{bestTrial.Seed:X}):");
            foreach (var t in bestTrial.Turns)
            {
                var played = t.Events.Where(e => e.Kind == PlayCapture.EventKind.Play).Select(e => e.Label);
                Console.WriteLine($"    Turn {t.Turn}: {t.Damage} dmg via [{string.Join(", ", played)}]");
            }
        }
        return summary;
    }

    public sealed record Summary
    {
        public required string DeckName { get; init; }
        public required string PolicyName { get; init; }
        public required int Trials { get; init; }
        public required int BestDamage { get; init; }
        public required double AverageDamage { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public DamagePerTurnSim.TrialResult? BestTrial { get; init; }
    }
}
