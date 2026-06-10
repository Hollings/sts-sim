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
/// Two modes:
///  - Single: just the baseline deck. Events: started → seed* → done.
///  - A/B (VariantDeck set): baseline then variant on identical shuffle seeds,
///    finished with a paired z-test verdict. Events: started → seed*(phase=base)
///    → phaseDone → seed*(phase=variant) → phaseDone → abDone.
///
/// The wire shapes (event "type" strings, field names) here are part of the
/// frontend contract — see www/app.js. Don't rename without coordinating.
/// </summary>
internal sealed class SimJob
{
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    /// <summary>When set, run an A/B comparison: Deck (baseline) vs this.</summary>
    public IReadOnlyList<Harness.DeckEntry>? VariantDeck { get; init; }
    /// <summary>Human-readable description of the A→B change, e.g. "−Defend ×1 · +Inflame ×1".</summary>
    public string? ChangeSummary { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public required string CharacterId { get; init; }
    public required Func<object, Task> BroadcastEvent { get; init; }

    public int Seeds { get; init; } = 200;
    public int K { get; init; } = 30;
    public int Turns { get; init; } = 5;
    public double Epsilon { get; init; } = 0.30;
    /// <summary>Adaptive early-stop patience for the inner K loop; 0 = fixed K.</summary>
    public int Patience { get; init; } = 0;

    public async Task Run(CancellationToken ct)
    {
        bool abTest = VariantDeck != null;
        await BroadcastEvent(new
        {
            type = "started",
            deckSize = Deck.Count,
            variantDeckSize = VariantDeck?.Count,
            abTest,
            changeSummary = ChangeSummary,
            character = CharacterId,
            seeds = Seeds,
            k = K,
            turns = Turns,
            epsilon = Epsilon,
            patience = Patience,
        });

        try
        {
            var characterType = Harness.ResolveCharacterType(CharacterId)
                ?? typeof(MegaCrit.Sts2.Core.Models.Characters.Ironclad);

            var baseSummary = await RunPhase("base", Deck, characterType, ct);
            ct.ThrowIfCancellationRequested();

            if (!abTest)
            {
                await BroadcastEvent(DoneEvent("done", "base", baseSummary));
                return;
            }

            var variantSummary = await RunPhase("variant", VariantDeck!, characterType, ct);
            ct.ThrowIfCancellationRequested();

            await BroadcastEvent(BuildAbVerdict(baseSummary, variantSummary));
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

    private async Task<BestOfKRunner.Summary> RunPhase(
        string phase, IReadOnlyList<Harness.DeckEntry> deck, Type characterType, CancellationToken ct)
    {
        var runner = new BestOfKRunner
        {
            DeckName = $"{CharacterId} ({phase})",
            Deck = deck,
            Relics = Relics,
            CharacterType = characterType,
            // Fresh policy per phase, but identical construction + identical
            // seed derivation inside the runner = a paired comparison.
            Policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), Epsilon),
            Seeds = Seeds,
            InnerSamples = K,
            Patience = Patience,
            Turns = Turns,
            Quiet = true,
            Cancellation = ct,
            OnSeedDone = p => OnSeedDone(phase, p),
            OnNewBest = t => OnNewBest(phase, t),
        };
        var summary = await runner.Run();
        if (!ct.IsCancellationRequested && VariantDeck != null)
            await BroadcastEvent(DoneEvent("phaseDone", phase, summary));
        return summary;
    }

    private object DoneEvent(string type, string phase, BestOfKRunner.Summary s) => new
    {
        type,
        phase,
        avgOfBest = s.AvgOfBest,
        avgPerTurn = Turns == 0 ? 0 : s.AvgOfBest / Turns,
        ci95 = s.Ci95HalfWidth,
        bestOfBest = s.BestOfBest,
        worstSeedBest = s.WorstSeedBest,
        totalRuns = s.TotalRuns,
        elapsedSec = s.Elapsed.TotalSeconds,
        medianConvergenceK = s.MedianConvergenceK,
        maxConvergenceK = s.MaxConvergenceK,
    };

    /// <summary>
    /// Paired z-test on per-seed bests. Seed index i used the same shuffle seed
    /// in both phases, so differencing cancels most shuffle luck — far tighter
    /// CI than the unpaired two-sample test for the same compute.
    /// </summary>
    private object BuildAbVerdict(BestOfKRunner.Summary baseS, BestOfKRunner.Summary varS)
    {
        int n = Math.Min(baseS.PerSeedBests.Count, varS.PerSeedBests.Count);
        var diffs = new double[n];
        for (int i = 0; i < n; i++)
            diffs[i] = varS.PerSeedBests[i] - baseS.PerSeedBests[i];

        double mean = n == 0 ? 0 : diffs.Average();
        double variance = n <= 1 ? 0 : diffs.Sum(d => (d - mean) * (d - mean)) / (n - 1);
        double stdErr = n == 0 ? 0 : Math.Sqrt(variance) / Math.Sqrt(n);
        double z = stdErr > 0 ? mean / stdErr : 0;
        double ci95 = 1.96 * stdErr;

        var (verdict, verdictClass) = z switch
        {
            > 2.5 => ("ADD IT — highly significant improvement", "good"),
            > 1.96 => ("ADD IT — significant at 95%", "good"),
            < -2.5 => ("DON'T — highly significant regression", "bad"),
            < -1.96 => ("DON'T — significant regression at 95%", "bad"),
            _ => ("INCONCLUSIVE — difference is within noise; run more seeds or the swap doesn't matter", "neutral"),
        };

        return new
        {
            type = "abDone",
            changeSummary = ChangeSummary,
            baseAvg = baseS.AvgOfBest,
            baseCi95 = baseS.Ci95HalfWidth,
            variantAvg = varS.AvgOfBest,
            variantCi95 = varS.Ci95HalfWidth,
            lift = mean,
            liftCi95 = ci95,
            liftPerTurn = Turns == 0 ? 0 : mean / Turns,
            z,
            pairedSeeds = n,
            verdict,
            verdictClass,
            totalRuns = baseS.TotalRuns + varS.TotalRuns,
            elapsedSec = baseS.Elapsed.TotalSeconds + varS.Elapsed.TotalSeconds,
        };
    }

    private void OnSeedDone(string phase, BestOfKRunner.SeedProgress p) => _ = BroadcastEvent(new
    {
        type = "seed",
        phase,
        index = p.SeedIndex,
        total = p.TotalSeeds,
        bestForSeed = p.BestForSeed,
        runningAvg = p.RunningAvg,
        runningStdErr = p.RunningStdErr,
        ci95 = 1.96 * p.RunningStdErr,
        totalRuns = p.TotalRuns,
        elapsedMs = (long)p.Elapsed.TotalMilliseconds,
    });

    private void OnNewBest(string phase, DamagePerTurnSim.TrialResult trial) => _ = BroadcastEvent(new
    {
        type = "newBest",
        phase,
        seed = trial.Seed,
        totalDamage = trial.TotalDamage,
        avgPerTurn = trial.AvgPerTurn,
        turns = trial.Turns.Select(t => new
        {
            turn = t.Turn,
            damage = t.Damage,
            // Chronological event timeline: each entry is { kind: "draw"|"play",
            // label, auto: true if this play came from an autoplay (Hellraiser
            // strike etc.) }. Enables the UI to render cascades top-to-bottom.
            events = t.Events.Select(e => new
            {
                kind = e.Kind == PlayCapture.EventKind.Draw ? "draw" : "play",
                label = e.Label,
                auto = e.Auto,
                subject = e.SubjectLabel,
            }),
        }),
    });
}
