using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StS2Sim.SilentTests;

/// <summary>
/// Top-level runner for every Silent card test bucket. Each bucket file
/// (Bucket1_BasicAttacks.cs, etc.) exposes a public static
/// <c>Task&lt;IReadOnlyList&lt;TestResult&gt;&gt; RunAll()</c> method.
///
/// Invoke from CLI: <c>dotnet run -c Release -- silent-tests</c>
/// </summary>
internal static class SilentTestsRunner
{
    public static async Task<int> RunAll()
    {
        Harness.Bootstrap();
        Console.WriteLine("=== StS2 Silent Card Test Battery ===\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Each bucket gets called sequentially so we don't fight over the
        // singleton CombatManager. The combats inside each bucket are also
        // sequential for the same reason.
        var buckets = new (string Name, Func<Task<IReadOnlyList<TestHelpers.TestResult>>> Run)[]
        {
            ("Bucket 1 - Basic Attacks",            Bucket1_BasicAttacks.RunAll),
            ("Bucket 2 - Multi-hit & Shiv Attacks", Bucket2_MultiHitShiv.RunAll),
            ("Bucket 3 - Conditional/Burst Attacks", Bucket3_ConditionalBurst.RunAll),
            ("Bucket 4 - Defensive & Draw Skills",  Bucket4_DefensiveDraw.RunAll),
            ("Bucket 5 - Utility & Card Manipulation", Bucket5_UtilityCardManip.RunAll),
            ("Bucket 6 - Powers & Poison Suite",    Bucket6_PowersPoison.RunAll),
        };

        var allResults = new List<(string Bucket, TestHelpers.TestResult Result)>();
        foreach (var (name, run) in buckets)
        {
            Console.WriteLine($"\n--- {name} ---");
            IReadOnlyList<TestHelpers.TestResult> results;
            try
            {
                results = await run();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  BUCKET CRASHED: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            foreach (var r in results)
            {
                allResults.Add((name, r));
                PrintResult(r);
            }
        }

        sw.Stop();
        PrintSummary(allResults, sw.Elapsed);

        // Return non-zero exit code if any tests crashed (real bugs). Failures
        // are expected during development; crashes are not.
        var crashCount = allResults.Count(x => x.Result.Outcome == TestHelpers.Outcome.Crash);
        return crashCount > 0 ? 2 : 0;
    }

    private static void PrintResult(TestHelpers.TestResult r)
    {
        var tag = r.Outcome switch
        {
            TestHelpers.Outcome.Pass    => "PASS",
            TestHelpers.Outcome.Fail    => "FAIL",
            TestHelpers.Outcome.Crash   => "CRASH",
            TestHelpers.Outcome.Skipped => "SKIP",
            _ => "?",
        };
        var detail = r.Detail is null ? "" : "  — " + r.Detail;
        Console.WriteLine($"  [{tag,-5}] {r.Name}{detail}");
    }

    private static void PrintSummary(List<(string Bucket, TestHelpers.TestResult Result)> all, TimeSpan elapsed)
    {
        Console.WriteLine();
        Console.WriteLine("=== Summary ===");

        var byBucket = all.GroupBy(x => x.Bucket);
        foreach (var g in byBucket)
        {
            var pass = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Pass);
            var fail = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Fail);
            var crash = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Crash);
            var skip = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Skipped);
            Console.WriteLine($"  {g.Key,-46}  pass={pass,3}  fail={fail,3}  crash={crash,3}  skip={skip,3}");
        }

        var totalPass = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Pass);
        var totalFail = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Fail);
        var totalCrash = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Crash);
        var totalSkip = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Skipped);
        Console.WriteLine();
        Console.WriteLine($"  TOTAL: {totalPass}/{all.Count} pass · {totalFail} fail · {totalCrash} crash · {totalSkip} skip");
        Console.WriteLine($"  Elapsed: {elapsed.TotalSeconds:F1}s");

        if (totalCrash > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== CRASHES (real bugs — fix the harness) ===");
            foreach (var (bucket, r) in all.Where(x => x.Result.Outcome == TestHelpers.Outcome.Crash))
                Console.WriteLine($"  [{bucket}] {r.Name} — {r.Detail}");
        }
    }
}
