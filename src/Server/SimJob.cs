using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace StS2Sim;

/// <summary>
/// One end-to-end run of <see cref="BestOfKRunner"/> with progress events
/// shaped for the web UI. Splits sim orchestration out of the HTTP layer so
/// the server only deals with routing/transport, not the algorithm.
///
/// The wire shapes (event "type" strings, field names) here are part of the
/// frontend contract — see www/app.js. Don't rename without coordinating.
/// </summary>
internal sealed class SimJob
{
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public required string CharacterId { get; init; }
    public required Func<object, Task> BroadcastEvent { get; init; }

    public int Seeds { get; init; } = 200;
    public int K { get; init; } = 30;
    public int Turns { get; init; } = 5;
    public double Epsilon { get; init; } = 0.30;

    public async Task Run(CancellationToken ct)
    {
        await BroadcastEvent(new
        {
            type = "started",
            deckSize = Deck.Count,
            character = CharacterId,
            seeds = Seeds,
            k = K,
            turns = Turns,
            epsilon = Epsilon,
        });

        try
        {
            var policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), Epsilon);
            var runner = new BestOfKRunner
            {
                DeckName = CharacterId,
                Deck = Deck,
                Policy = policy,
                Seeds = Seeds,
                InnerSamples = K,
                Turns = Turns,
                Quiet = true,
                Cancellation = ct,
                OnSeedDone = OnSeedDone,
                OnNewBest = OnNewBest,
            };
            var summary = await runner.Run();
            await BroadcastEvent(new
            {
                type = "done",
                avgOfBest = summary.AvgOfBest,
                avgPerTurn = summary.AvgOfBest / summary.Seeds == 0 ? 0 : summary.AvgOfBest / Turns,
                ci95 = summary.Ci95HalfWidth,
                bestOfBest = summary.BestOfBest,
                worstSeedBest = summary.WorstSeedBest,
                totalRuns = summary.TotalRuns,
                elapsedSec = summary.Elapsed.TotalSeconds,
                medianConvergenceK = summary.MedianConvergenceK,
                maxConvergenceK = summary.MaxConvergenceK,
            });
        }
        catch (OperationCanceledException)
        {
            await BroadcastEvent(new { type = "cancelled" });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[SimJob] sim run threw:\n" + ex);
            await BroadcastEvent(new { type = "error", message = ex.Message, stack = ex.ToString() });
        }
    }

    private void OnSeedDone(BestOfKRunner.SeedProgress p) => _ = BroadcastEvent(new
    {
        type = "seed",
        index = p.SeedIndex,
        total = p.TotalSeeds,
        bestForSeed = p.BestForSeed,
        runningAvg = p.RunningAvg,
        runningStdErr = p.RunningStdErr,
        ci95 = 1.96 * p.RunningStdErr,
        totalRuns = p.TotalRuns,
        elapsedMs = (long)p.Elapsed.TotalMilliseconds,
    });

    private void OnNewBest(DamagePerTurnSim.TrialResult trial) => _ = BroadcastEvent(new
    {
        type = "newBest",
        seed = trial.Seed,
        totalDamage = trial.TotalDamage,
        avgPerTurn = trial.AvgPerTurn,
        turns = trial.Turns.Select(t => new
        {
            turn = t.Turn,
            damage = t.Damage,
            hand = t.Hand,
            played = t.CardsPlayed,
        }),
    });
}
