using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Runner for the Regent / Necrobinder / Defect card batteries — the targeted
/// "fragile mechanics" complement to the broader Silent battery. Same result
/// semantics: Fail = wrong value (informative), Crash = harness bug (exit 2).
///
/// Invoke from CLI: <c>dotnet run -c Release -- char-tests</c>
/// </summary>
internal static class CharTestsRunner
{
    public static async Task<int> RunAll()
    {
        Harness.Bootstrap();
        Console.WriteLine("=== StS2 Character Mechanics Test Battery (Regent / Necrobinder / Defect) ===\n");

        var sw = System.Diagnostics.Stopwatch.StartNew();

        var buckets = new (string Name, Func<Task<IReadOnlyList<TestHelpers.TestResult>>> Run)[]
        {
            ("Defect - Orbs & Focus",        DefectTests.RunAll),
            ("Necrobinder - Osty & Souls",   NecrobinderTests.RunAll),
            ("Regent - Stars & Forge",       RegentTests.RunAll),
            ("Enchantments",                 EnchantmentTests.RunAll),
            ("Boss mechanics",               BossMechanicsTests.RunAll),
        };

        var all = new List<(string Bucket, TestHelpers.TestResult Result)>();
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
                all.Add((name, r));
                var tag = r.Outcome switch
                {
                    TestHelpers.Outcome.Pass => "PASS",
                    TestHelpers.Outcome.Fail => "FAIL",
                    TestHelpers.Outcome.Crash => "CRASH",
                    TestHelpers.Outcome.Skipped => "SKIP",
                    _ => "?",
                };
                var detail = r.Detail is null ? "" : "  — " + r.Detail;
                Console.WriteLine($"  [{tag,-5}] {r.Name}{detail}");
            }
        }

        sw.Stop();

        Console.WriteLine();
        Console.WriteLine("=== Summary ===");
        foreach (var g in all.GroupBy(x => x.Bucket))
        {
            var pass = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Pass);
            var fail = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Fail);
            var crash = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Crash);
            var skip = g.Count(x => x.Result.Outcome == TestHelpers.Outcome.Skipped);
            Console.WriteLine($"  {g.Key,-34}  pass={pass,3}  fail={fail,3}  crash={crash,3}  skip={skip,3}");
        }

        var totalPass = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Pass);
        var totalCrash = all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Crash);
        Console.WriteLine();
        Console.WriteLine($"  TOTAL: {totalPass}/{all.Count} pass · " +
            $"{all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Fail)} fail · " +
            $"{totalCrash} crash · " +
            $"{all.Count(x => x.Result.Outcome == TestHelpers.Outcome.Skipped)} skip");
        Console.WriteLine($"  Elapsed: {sw.Elapsed.TotalSeconds:F1}s");

        if (totalCrash > 0)
        {
            Console.WriteLine();
            Console.WriteLine("=== CRASHES (real bugs — fix the harness) ===");
            foreach (var (bucket, r) in all.Where(x => x.Result.Outcome == TestHelpers.Outcome.Crash))
                Console.WriteLine($"  [{bucket}] {r.Name} — {r.Detail}");
        }

        return totalCrash > 0 ? 2 : 0;
    }
}
