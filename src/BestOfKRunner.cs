using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// "Average of best-of-K" estimator. For each shuffle seed, run K random-play
/// attempts and keep the best damage. Average those per-seed maxima across
/// many seeds. The result approximates "average outcome under near-optimal
/// play", separating shuffle variance (kept) from play-decision variance
/// (averaged out by maxing).
/// </summary>
internal sealed class BestOfKRunner
{
    public required string DeckName { get; init; }
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public Type CharacterType { get; init; } = typeof(MegaCrit.Sts2.Core.Models.Characters.Ironclad);
    public IPlayPolicy Policy { get; init; } = new RandomPolicy();
    public int Turns { get; init; } = 5;
    public int HandSize { get; init; } = 5;
    public int Seeds { get; init; } = 100;
    public int InnerSamples { get; init; } = 100;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool Quiet { get; init; } = false;
    /// <summary>Fires after each seed completes K samples — for live UI updates.</summary>
    public Action<SeedProgress>? OnSeedDone { get; init; }
    /// <summary>Fires when a new best-of-best damage trial is found (full per-turn breakdown).</summary>
    public Action<DamagePerTurnSim.TrialResult>? OnNewBest { get; init; }
    /// <summary>Optional cancellation — UI Stop button hooks here.</summary>
    public System.Threading.CancellationToken Cancellation { get; init; } = default;

    public sealed record SeedProgress(
        int SeedIndex,
        int TotalSeeds,
        int BestForSeed,
        double RunningAvg,
        double RunningStdErr,
        long TotalRuns,
        TimeSpan Elapsed);

    public async Task<Summary> Run()
    {
        var sw = Stopwatch.StartNew();
        var perSeedBest = new int[Seeds];
        var perSeedSampleK = new int[Seeds]; // K at which best was first found, for convergence inspection
        int completed = 0;
        long totalRuns = 0;
        var lastPrint = TimeSpan.Zero;
        DamagePerTurnSim.TrialResult? globalBest = null;

        if (!Quiet) Console.WriteLine($"\n=== Best-of-K: {DeckName} | policy={Policy.Name} | seeds={Seeds} × K={InnerSamples} ===");

        for (int s = 0; s < Seeds; s++)
        {
            var shuffleSeed = unchecked((uint)(s * 0x9E3779B1u + 0xC0FFEEu));
            int seedBest = int.MinValue;
            int firstFoundAt = 0;

            for (int k = 0; k < InnerSamples; k++)
            {
                var policySeed = unchecked((uint)((s * 1024 + k) * 0x85EBCA77u + 0xBADCAFEu));
                var sim = new DamagePerTurnSim
                {
                    DeckName = DeckName,
                    Deck = Deck,
                    Relics = Relics,
                    CharacterType = CharacterType,
                    Turns = Turns,
                    HandSize = HandSize,
                    Policy = Policy,
                    PolicyRngSeed = policySeed,
                };
                var result = await sim.RunSingleTrial(shuffleSeed);
                totalRuns++;
                if (result.TotalDamage > seedBest)
                {
                    seedBest = result.TotalDamage;
                    firstFoundAt = k + 1;
                }
                if (globalBest == null || result.TotalDamage > globalBest.TotalDamage)
                {
                    globalBest = result;
                    OnNewBest?.Invoke(result);
                }
            }
            perSeedBest[s] = seedBest;
            perSeedSampleK[s] = firstFoundAt;
            completed = s + 1;

            if (OnSeedDone != null)
            {
                var bestsSoFar = perSeedBest.Take(s + 1).ToList();
                var rAvg = bestsSoFar.Average();
                var rVar = s == 0 ? 0 : bestsSoFar.Select(x => Math.Pow(x - rAvg, 2)).Sum() / s;
                var rStdErr = s == 0 ? 0 : Math.Sqrt(rVar) / Math.Sqrt(s + 1);
                OnSeedDone(new SeedProgress(s, Seeds, seedBest, rAvg, rStdErr, totalRuns, sw.Elapsed));
            }

            if (!Quiet && sw.Elapsed - lastPrint >= ProgressInterval)
            {
                var seenBests = perSeedBest.Take(s + 1).ToList();
                Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F1}s  seeds={s + 1}/{Seeds}  runs={totalRuns}  avg-of-best={seenBests.Average():F1}  best-of-best={seenBests.Max()}  rate={totalRuns / sw.Elapsed.TotalSeconds:F0}/s");
                lastPrint = sw.Elapsed;
            }

            if (Cancellation.IsCancellationRequested) break;
        }
        sw.Stop();

        var bests = perSeedBest.Take(completed).ToList();
        var ks = perSeedSampleK.Take(completed).OrderBy(x => x).ToList();
        var avg = bests.Count == 0 ? 0 : bests.Average();
        var best = bests.Count == 0 ? 0 : bests.Max();
        var worstSeedBest = bests.Count == 0 ? 0 : bests.Min();
        var medianFoundAt = ks.Count == 0 ? 0 : ks[ks.Count / 2];
        var maxFoundAt = ks.Count == 0 ? 0 : ks.Max();
        var variance = bests.Count <= 1 ? 0 : bests.Select(x => Math.Pow(x - avg, 2)).Sum() / (bests.Count - 1);
        var stdDev = Math.Sqrt(variance);
        var stdErr = bests.Count == 0 ? 0 : stdDev / Math.Sqrt(bests.Count);
        var ci95 = 1.96 * stdErr;

        if (!Quiet)
        {
            Console.WriteLine();
            Console.WriteLine($"  Done. {totalRuns} runs in {sw.Elapsed.TotalSeconds:F1}s ({totalRuns / sw.Elapsed.TotalSeconds:F0}/s)");
            Console.WriteLine($"  Avg-of-best:        {avg:F2} ± {ci95:F2}  (95% CI)  ({avg / Turns:F2}/turn)");
            Console.WriteLine($"  Std deviation across seeds: {stdDev:F2} (variance source: shuffle luck)");
            Console.WriteLine($"  Best/worst seed:    {best} / {worstSeedBest}");
            Console.WriteLine($"  Convergence: median seed found its best at K={medianFoundAt}, slowest at K={maxFoundAt}");

            if (maxFoundAt < InnerSamples * 0.5)
                Console.WriteLine($"  → All seeds converged early; K={InnerSamples} was overkill. Try K={maxFoundAt * 2}.");
            else if (maxFoundAt >= InnerSamples - 5)
                Console.WriteLine($"  → Some seeds were still improving at K={InnerSamples}. Bump K higher.");
        }

        return new Summary
        {
            DeckName = DeckName,
            PolicyName = Policy.Name,
            Seeds = Seeds,
            InnerSamples = InnerSamples,
            AvgOfBest = avg,
            StdErrOfMean = stdErr,
            Ci95HalfWidth = ci95,
            StdDevAcrossSeeds = stdDev,
            BestOfBest = best,
            WorstSeedBest = worstSeedBest,
            MedianConvergenceK = medianFoundAt,
            MaxConvergenceK = maxFoundAt,
            Elapsed = sw.Elapsed,
            TotalRuns = totalRuns,
        };
    }

    public sealed record Summary
    {
        public required string DeckName { get; init; }
        public required string PolicyName { get; init; }
        public required int Seeds { get; init; }
        public required int InnerSamples { get; init; }
        public required double AvgOfBest { get; init; }
        public required double StdErrOfMean { get; init; }
        public required double Ci95HalfWidth { get; init; }
        public required double StdDevAcrossSeeds { get; init; }
        public required int BestOfBest { get; init; }
        public required int WorstSeedBest { get; init; }
        public required int MedianConvergenceK { get; init; }
        public required int MaxConvergenceK { get; init; }
        public required TimeSpan Elapsed { get; init; }
        public required long TotalRuns { get; init; }
    }
}
