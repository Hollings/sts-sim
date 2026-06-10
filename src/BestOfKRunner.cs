using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// "Average of best-of-K" estimator. For each shuffle seed, run K random-play
/// attempts and keep the best score. Average those per-seed maxima across
/// many seeds. The result approximates "average outcome under near-optimal
/// play", separating shuffle variance (kept) from play-decision variance
/// (averaged out by maxing).
///
/// Two metrics, chosen by <see cref="EncounterId"/>:
///  - null → damage vs the 9999-HP dummy over a fixed number of turns
///    (<see cref="DamagePerTurnSim"/>); score = total damage.
///  - set  → a full simulated fight against that encounter
///    (<see cref="EncounterSim"/>); score = +HP kept on win / −boss HP on loss.
/// </summary>
internal sealed class BestOfKRunner
{
    public required string DeckName { get; init; }
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public Type CharacterType { get; init; } = typeof(MegaCrit.Sts2.Core.Models.Characters.Ironclad);
    public IPlayPolicy Policy { get; init; } = new RandomPolicy();
    /// <summary>Null = dummy damage mode. Otherwise a full fight vs this encounter.</summary>
    public string? EncounterId { get; init; }
    /// <summary>Dummy mode: turns per trial. Encounter mode: the turn cap (reaching it = loss).</summary>
    public int Turns { get; init; } = 5;
    public int HandSize { get; init; } = 5;
    public int Seeds { get; init; } = 100;
    public int InnerSamples { get; init; } = 100;
    /// <summary>
    /// Adaptive early-stop: 0 = always run all K samples. Otherwise, stop a
    /// seed's inner sampling once this many consecutive samples fail to improve
    /// that seed's best. Cuts compute 3-10x on easy seeds while keeping K as
    /// the hard cap. Slight low bias vs full K — but identical settings on both
    /// sides of an A/B comparison cancel it.
    /// </summary>
    public int Patience { get; init; } = 0;
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromSeconds(2);
    public bool Quiet { get; init; } = false;
    /// <summary>Fires after each seed completes K samples — for live UI updates.</summary>
    public Action<SeedProgress>? OnSeedDone { get; init; }
    /// <summary>Fires when a new best-of-best trial is found (full per-turn breakdown).</summary>
    public Action<TrialOutcome>? OnNewBest { get; init; }
    /// <summary>Optional cancellation — UI Stop button hooks here.</summary>
    public System.Threading.CancellationToken Cancellation { get; init; } = default;

    /// <summary>
    /// One trial's result, metric-agnostic. Win/PlayerHp/EnemyHp are only
    /// meaningful in encounter mode (Win == null in dummy mode).
    /// </summary>
    public sealed record TrialOutcome(
        uint Seed,
        int Score,
        bool? Win,
        int PlayerHpRemaining,
        int PlayerMaxHp,
        int EnemyHpRemaining,
        IReadOnlyList<DamagePerTurnSim.TurnResult> Turns);

    public sealed record SeedProgress(
        int SeedIndex,
        int TotalSeeds,
        int BestForSeed,
        bool? BestForSeedWin,
        double RunningAvg,
        double RunningStdErr,
        int WinnableSeedsSoFar,
        long TotalRuns,
        TimeSpan Elapsed);

    private async Task<TrialOutcome> RunTrial(uint shuffleSeed, uint policySeed)
    {
        if (EncounterId == null)
        {
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
            var r = await sim.RunSingleTrial(shuffleSeed);
            return new TrialOutcome(shuffleSeed, r.TotalDamage, Win: null, 0, 0, 0, r.Turns);
        }
        else
        {
            var sim = new EncounterSim
            {
                Deck = Deck,
                EncounterId = EncounterId,
                Relics = Relics,
                CharacterType = CharacterType,
                MaxTurns = Turns,
                HandSize = HandSize,
                Policy = Policy,
                PolicyRngSeed = policySeed,
            };
            var r = await sim.RunSingleTrial(shuffleSeed);
            return new TrialOutcome(shuffleSeed, r.Score, r.Win, r.PlayerHpRemaining, r.PlayerMaxHp, r.EnemyHpRemaining, r.Turns);
        }
    }

    public async Task<Summary> Run()
    {
        var sw = Stopwatch.StartNew();
        var perSeedBest = new int[Seeds];
        var perSeedBestWin = new bool?[Seeds];
        var perSeedSampleK = new int[Seeds]; // K at which best was first found, for convergence inspection
        int completed = 0;
        long totalRuns = 0;
        var lastPrint = TimeSpan.Zero;
        TrialOutcome? globalBest = null;
        // Running sums for progress stats — avoids re-scanning the per-seed
        // array on every seed (O(n^2) over a long run).
        double runningSum = 0, runningSumSq = 0;
        int runningMax = int.MinValue;
        int winnableSeeds = 0;

        if (!Quiet) Console.WriteLine($"\n=== Best-of-K: {DeckName} | policy={Policy.Name} | seeds={Seeds} × K={InnerSamples}{(EncounterId != null ? $" | vs {EncounterId}" : "")} ===");

        for (int s = 0; s < Seeds; s++)
        {
            var shuffleSeed = unchecked((uint)(s * 0x9E3779B1u + 0xC0FFEEu));
            int seedBest = int.MinValue;
            bool? seedBestWin = null;
            int firstFoundAt = 0;

            for (int k = 0; k < InnerSamples; k++)
            {
                var policySeed = unchecked((uint)((s * 1024 + k) * 0x85EBCA77u + 0xBADCAFEu));
                var result = await RunTrial(shuffleSeed, policySeed);
                totalRuns++;
                if (result.Score > seedBest)
                {
                    seedBest = result.Score;
                    seedBestWin = result.Win;
                    firstFoundAt = k + 1;
                }
                if (globalBest == null || result.Score > globalBest.Score)
                {
                    globalBest = result;
                    OnNewBest?.Invoke(result);
                }
                if (Patience > 0 && k + 1 - firstFoundAt >= Patience)
                    break; // this seed has gone Patience samples without improving
            }
            perSeedBest[s] = seedBest;
            perSeedBestWin[s] = seedBestWin;
            perSeedSampleK[s] = firstFoundAt;
            completed = s + 1;
            runningSum += seedBest;
            runningSumSq += (double)seedBest * seedBest;
            if (seedBest > runningMax) runningMax = seedBest;
            if (seedBestWin == true) winnableSeeds++;

            if (OnSeedDone != null)
            {
                int n = s + 1;
                var rAvg = runningSum / n;
                var rVar = n == 1 ? 0 : Math.Max(0, runningSumSq - runningSum * runningSum / n) / (n - 1);
                var rStdErr = Math.Sqrt(rVar) / Math.Sqrt(n);
                OnSeedDone(new SeedProgress(s, Seeds, seedBest, seedBestWin, rAvg, rStdErr, winnableSeeds, totalRuns, sw.Elapsed));
            }

            if (!Quiet && sw.Elapsed - lastPrint >= ProgressInterval)
            {
                Console.WriteLine($"  t={sw.Elapsed.TotalSeconds:F1}s  seeds={s + 1}/{Seeds}  runs={totalRuns}  avg-of-best={runningSum / (s + 1):F1}  best-of-best={runningMax}  rate={totalRuns / sw.Elapsed.TotalSeconds:F0}/s");
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
            Console.WriteLine($"  Avg-of-best:        {avg:F2} ± {ci95:F2}  (95% CI)");
            if (EncounterId != null)
                Console.WriteLine($"  Winnable seeds:     {winnableSeeds}/{completed} ({(completed == 0 ? 0 : 100.0 * winnableSeeds / completed):F0}%)");
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
            PerSeedBests = bests,
            WinnableSeeds = EncounterId != null ? winnableSeeds : null,
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
        /// <summary>Per-seed best score, in seed order. Lets A/B callers run a
        /// paired test (same seed index = same shuffle seed on both sides).</summary>
        public required IReadOnlyList<int> PerSeedBests { get; init; }
        /// <summary>Encounter mode: seeds whose best outcome was a win. Null in dummy mode.</summary>
        public required int? WinnableSeeds { get; init; }
    }
}
