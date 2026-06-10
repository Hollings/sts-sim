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
/// Three modes:
///  - Single: just the baseline deck. Events: started → seed* → done.
///  - A/B (VariantDeck set): baseline then variant on identical shuffle seeds,
///    finished with a paired z-test verdict. Events: started → seed*(phase=base)
///    → phaseDone → seed*(phase=variant) → phaseDone → abDone.
///  - Compare (Candidates set): baseline then one phase per candidate card,
///    every phase on identical shuffle seeds. Finishes with a ranking of all
///    candidates by paired lift vs baseline, plus a winner-vs-runner-up paired
///    test — the "which of these reward cards do I take?" button. Events:
///    started → [seed* → phaseDone] per phase → compareDone.
///
/// All phases share the same seed derivation inside BestOfKRunner, so seed
/// index i is the same shuffle on every side — that's what makes the paired
/// statistics legal.
///
/// The wire shapes (event "type" strings, field names) here are part of the
/// frontend contract — see www/app.js. Don't rename without coordinating.
/// </summary>
internal sealed class SimJob
{
    /// <summary>One compare-mode candidate: baseline deck plus one card.</summary>
    public sealed record Candidate(string Label, IReadOnlyList<Harness.DeckEntry> Deck);

    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    /// <summary>When set (and Candidates empty), run an A/B comparison: Deck (baseline) vs this.</summary>
    public IReadOnlyList<Harness.DeckEntry>? VariantDeck { get; init; }
    /// <summary>When non-empty, run compare mode: Deck (baseline) vs each candidate.</summary>
    public IReadOnlyList<Candidate> Candidates { get; init; } = Array.Empty<Candidate>();
    /// <summary>Human-readable description of the common deck edit, e.g. "−Defend ×1 · +Inflame ×1".</summary>
    public string? ChangeSummary { get; init; }
    /// <summary>Null = damage vs dummy. Otherwise every phase fights this encounter.</summary>
    public string? EncounterId { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public required string CharacterId { get; init; }
    public required Func<object, Task> BroadcastEvent { get; init; }

    public int Seeds { get; init; } = 200;
    public int K { get; init; } = 30;
    public int Turns { get; init; } = 5;
    public double Epsilon { get; init; } = 0.30;
    /// <summary>Adaptive early-stop patience for the inner K loop; 0 = fixed K.</summary>
    public int Patience { get; init; } = 0;

    private bool IsCompare => Candidates.Count > 0;
    private bool IsAb => !IsCompare && VariantDeck != null;
    private bool IsMultiPhase => IsCompare || IsAb;

    public async Task Run(CancellationToken ct)
    {
        await BroadcastEvent(new
        {
            type = "started",
            mode = IsCompare ? "compare" : IsAb ? "ab" : "single",
            metric = EncounterId != null ? "score" : "damage",
            encounterId = EncounterId,
            encounterName = EncounterId != null ? CardLabels.PrettyName(EncounterId) : null,
            deckSize = Deck.Count,
            variantDeckSize = VariantDeck?.Count,
            abTest = IsAb, // legacy field, kept for wire compat
            candidates = Candidates.Select(c => c.Label).ToList(),
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

            var baseSummary = await RunPhase("base", "current deck", Deck, characterType, ct);
            ct.ThrowIfCancellationRequested();

            if (IsCompare)
            {
                var candSummaries = new List<(Candidate Cand, BestOfKRunner.Summary Summary)>();
                for (int i = 0; i < Candidates.Count; i++)
                {
                    var cand = Candidates[i];
                    var summary = await RunPhase($"cand{i}", cand.Label, cand.Deck, characterType, ct);
                    ct.ThrowIfCancellationRequested();
                    candSummaries.Add((cand, summary));
                }
                await BroadcastEvent(BuildCompareVerdict(baseSummary, candSummaries));
            }
            else if (IsAb)
            {
                var variantSummary = await RunPhase("variant", "variant deck", VariantDeck!, characterType, ct);
                ct.ThrowIfCancellationRequested();
                await BroadcastEvent(BuildAbVerdict(baseSummary, variantSummary));
            }
            else
            {
                await BroadcastEvent(DoneEvent("done", "base", "current deck", baseSummary));
            }
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
        string phase, string label, IReadOnlyList<Harness.DeckEntry> deck, Type characterType, CancellationToken ct)
    {
        var runner = new BestOfKRunner
        {
            DeckName = $"{CharacterId} ({label})",
            Deck = deck,
            Relics = Relics,
            CharacterType = characterType,
            EncounterId = EncounterId,
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
            OnNewBest = t => OnNewBest(phase, label, t),
        };
        var summary = await runner.Run();
        if (!ct.IsCancellationRequested && IsMultiPhase)
            await BroadcastEvent(DoneEvent("phaseDone", phase, label, summary));
        return summary;
    }

    private object DoneEvent(string type, string phase, string label, BestOfKRunner.Summary s) => new
    {
        type,
        phase,
        label,
        avgOfBest = s.AvgOfBest,
        avgPerTurn = Turns == 0 ? 0 : s.AvgOfBest / Turns,
        ci95 = s.Ci95HalfWidth,
        bestOfBest = s.BestOfBest,
        worstSeedBest = s.WorstSeedBest,
        winnableSeeds = s.WinnableSeeds,
        winRate = s.WinnableSeeds is int w && s.PerSeedBests.Count > 0 ? (double?)w / s.PerSeedBests.Count : null,
        totalRuns = s.TotalRuns,
        elapsedSec = s.Elapsed.TotalSeconds,
        medianConvergenceK = s.MedianConvergenceK,
        maxConvergenceK = s.MaxConvergenceK,
    };

    /// <summary>Fraction of seeds whose best outcome was a win; null in dummy mode.</summary>
    private static double? WinRate(BestOfKRunner.Summary s)
        => s.WinnableSeeds is int w && s.PerSeedBests.Count > 0 ? (double?)w / s.PerSeedBests.Count : null;

    /// <summary>Paired stats between two per-seed best series (B − A).</summary>
    private static (double Lift, double StdErr, double Z, int N) PairedDiff(
        IReadOnlyList<int> a, IReadOnlyList<int> b)
    {
        int n = Math.Min(a.Count, b.Count);
        if (n == 0) return (0, 0, 0, 0);
        var diffs = new double[n];
        for (int i = 0; i < n; i++) diffs[i] = b[i] - a[i];
        double mean = diffs.Average();
        double variance = n <= 1 ? 0 : diffs.Sum(d => (d - mean) * (d - mean)) / (n - 1);
        double stdErr = Math.Sqrt(variance) / Math.Sqrt(n);
        double z = stdErr > 0 ? mean / stdErr : 0;
        return (mean, stdErr, z, n);
    }

    /// <summary>
    /// Paired z-test on per-seed bests. Seed index i used the same shuffle seed
    /// in both phases, so differencing cancels most shuffle luck — far tighter
    /// CI than the unpaired two-sample test for the same compute.
    /// </summary>
    private object BuildAbVerdict(BestOfKRunner.Summary baseS, BestOfKRunner.Summary varS)
    {
        var (lift, stdErr, z, n) = PairedDiff(baseS.PerSeedBests, varS.PerSeedBests);
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
            baseWinRate = WinRate(baseS),
            variantAvg = varS.AvgOfBest,
            variantCi95 = varS.Ci95HalfWidth,
            variantWinRate = WinRate(varS),
            lift,
            liftCi95 = ci95,
            liftPerTurn = Turns == 0 ? 0 : lift / Turns,
            z,
            pairedSeeds = n,
            verdict,
            verdictClass,
            totalRuns = baseS.TotalRuns + varS.TotalRuns,
            elapsedSec = baseS.Elapsed.TotalSeconds + varS.Elapsed.TotalSeconds,
        };
    }

    /// <summary>
    /// Rank every candidate by paired lift vs baseline, then ask the question
    /// that actually matters at a card reward: is the winner's edge over the
    /// runner-up real, and does anything beat skipping at all?
    /// </summary>
    private object BuildCompareVerdict(
        BestOfKRunner.Summary baseS,
        IReadOnlyList<(Candidate Cand, BestOfKRunner.Summary Summary)> cands)
    {
        var results = cands.Select((c, i) =>
        {
            var (lift, stdErr, z, _) = PairedDiff(baseS.PerSeedBests, c.Summary.PerSeedBests);
            return new
            {
                phase = $"cand{i}",
                label = c.Cand.Label,
                avg = c.Summary.AvgOfBest,
                ci95 = c.Summary.Ci95HalfWidth,
                winRate = WinRate(c.Summary),
                lift,
                liftCi95 = 1.96 * stdErr,
                liftPerTurn = Turns == 0 ? 0 : lift / Turns,
                z,
                beatsBase = z > 1.96,
                summary = c.Summary,
            };
        })
        .OrderByDescending(r => r.lift)
        .ToList();

        var winner = results[0];
        object? winnerVsRunnerUp = null;
        double wvrZ = double.PositiveInfinity; // single candidate: no runner-up to beat
        if (results.Count > 1)
        {
            var runnerUp = results[1];
            var (lift, stdErr, z, _) = PairedDiff(runnerUp.summary.PerSeedBests, winner.summary.PerSeedBests);
            wvrZ = z;
            winnerVsRunnerUp = new
            {
                winnerLabel = winner.label,
                runnerUpLabel = runnerUp.label,
                lift,
                liftCi95 = 1.96 * stdErr,
                z,
            };
        }

        string verdict;
        string verdictClass;
        if (winner.beatsBase && wvrZ > 1.96)
        {
            verdict = $"TAKE {winner.label.ToUpperInvariant()} — beats your current deck"
                + (results.Count > 1 ? " and clearly beats the other option(s)" : "");
            verdictClass = "good";
        }
        else if (winner.beatsBase)
        {
            var alsoGood = results.Skip(1).Where(r => r.beatsBase).Select(r => r.label).ToList();
            verdict = alsoGood.Count > 0
                ? $"TAKE {winner.label.ToUpperInvariant()} or {string.Join(" / ", alsoGood)} — all beat skipping, too close to call between them"
                : $"TAKE {winner.label.ToUpperInvariant()} — best of the bunch, though its edge over the runner-up isn't decisive";
            verdictClass = "good";
        }
        else if (results.All(r => r.z < -1.96))
        {
            verdict = "SKIP — every candidate makes the deck worse";
            verdictClass = "bad";
        }
        else
        {
            verdict = "INCONCLUSIVE — no candidate clearly beats your current deck; run more seeds or the choice genuinely doesn't matter much";
            verdictClass = "neutral";
        }

        return new
        {
            type = "compareDone",
            changeSummary = ChangeSummary,
            baseAvg = baseS.AvgOfBest,
            baseCi95 = baseS.Ci95HalfWidth,
            results = results.Select(r => new
            {
                r.phase, r.label, r.avg, r.ci95, r.winRate, r.lift, r.liftCi95, r.liftPerTurn, r.z, r.beatsBase,
            }).ToList(),
            baseWinRate = WinRate(baseS),
            winnerLabel = winner.label,
            winnerVsRunnerUp,
            verdict,
            verdictClass,
            pairedSeeds = Math.Min(baseS.PerSeedBests.Count, cands.Min(c => c.Summary.PerSeedBests.Count)),
            totalRuns = baseS.TotalRuns + cands.Sum(c => c.Summary.TotalRuns),
            elapsedSec = baseS.Elapsed.TotalSeconds + cands.Sum(c => c.Summary.Elapsed.TotalSeconds),
        };
    }

    private void OnSeedDone(string phase, BestOfKRunner.SeedProgress p) => _ = BroadcastEvent(new
    {
        type = "seed",
        phase,
        index = p.SeedIndex,
        total = p.TotalSeeds,
        bestForSeed = p.BestForSeed,
        bestForSeedWin = p.BestForSeedWin,
        winnableSeeds = EncounterId != null ? (int?)p.WinnableSeedsSoFar : null,
        runningAvg = p.RunningAvg,
        runningStdErr = p.RunningStdErr,
        ci95 = 1.96 * p.RunningStdErr,
        totalRuns = p.TotalRuns,
        elapsedMs = (long)p.Elapsed.TotalMilliseconds,
    });

    private void OnNewBest(string phase, string label, BestOfKRunner.TrialOutcome trial) => _ = BroadcastEvent(new
    {
        type = "newBest",
        phase,
        label,
        seed = trial.Seed,
        // In dummy mode Score IS total damage; the field name is part of the
        // wire contract so it stays. Encounter fields ride alongside.
        totalDamage = trial.Score,
        win = trial.Win,
        playerHpRemaining = trial.PlayerHpRemaining,
        playerMaxHp = trial.PlayerMaxHp,
        enemyHpRemaining = trial.EnemyHpRemaining,
        avgPerTurn = trial.Turns.Count == 0 ? 0 : (double)trial.Turns.Sum(t => t.Damage) / trial.Turns.Count,
        turns = trial.Turns.Select(t => new
        {
            turn = t.Turn,
            damage = t.Damage,
            // Chronological event timeline: each entry is { kind: "draw"|"play"|
            // "enemy", label, auto, subject }. Enables the UI to render
            // cascades and enemy moves top-to-bottom.
            events = t.Events.Select(e => new
            {
                kind = e.Kind switch
                {
                    PlayCapture.EventKind.Draw => "draw",
                    PlayCapture.EventKind.EnemyMove => "enemy",
                    _ => "play",
                },
                label = e.Label,
                auto = e.Auto,
                subject = e.SubjectLabel,
            }),
        }),
    });
}
