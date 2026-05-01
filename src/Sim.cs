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

        // === Two test decks ===
        // The first is "all damage cards" — greedy is approximately optimal.
        // The second has Inflame, which deals 0 damage but applies +2 Strength,
        // amplifying every later attack. Greedy will skip Inflame; ε-greedy
        // should occasionally try it and find a better ceiling.
        var simpleDeck = new List<Type>
        {
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(Bash),
        };
        var setupDeck = new List<Type>
        {
            typeof(Inflame), typeof(Inflame),                                         // setup
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),   // payoff
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(Bash), typeof(Bash),
        };

        // Skip the policy sweep on this run — keep it focused on the deck-comparison flow.
        // (The previous run already showed: ε=0 fine for damage decks, ε≥0.2 needed for setup.)

        // === K-vs-accuracy: how does compute budget affect verdict reliability? ===
        await KvsAccuracyCurve(setupDeck, "setup deck");

        // === The actual button: "should I add Inflame to my deck?" ===
        // Same 10 cards, swap one Defend for one Inflame. Compare under
        // ε-greedy (since pure greedy can't see setup cards).
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine("  THE BUTTON: should I add Inflame in place of a Defend?");
        Console.WriteLine("==========================================================");
        var deckA = new List<Type>
        {
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(Bash),
        };
        var deckB = new List<Type>
        {
            typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(StrikeIronclad), typeof(StrikeIronclad),
            typeof(DefendIronclad), typeof(DefendIronclad), typeof(DefendIronclad),
            typeof(Inflame),    // <-- swapped one Defend for one Inflame
            typeof(Bash),
        };
        await DeckAvsB(deckA, "starter (4 Defend)", deckB, "starter -1 Defend +1 Inflame");
    }

    private static async Task KvsAccuracyCurve(IReadOnlyList<Type> deck, string name)
    {
        Console.WriteLine();
        Console.WriteLine("==========================================================");
        Console.WriteLine($"  K-vs-accuracy: how much compute do we need? ({name})");
        Console.WriteLine("==========================================================");
        var policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.30);
        var ks = new[] { 1, 5, 10, 30, 100, 300 };
        Console.WriteLine($"{"K",4} {"avg-of-best",14} {"95% CI",10} {"runs",10} {"sec",6}");
        foreach (var k in ks)
        {
            var runner = new BestOfKRunner
            {
                DeckName = name,
                Deck = Harness.AsEntries(deck),
                Policy = policy,
                Seeds = 200,
                InnerSamples = k,
                Turns = 5,
                Quiet = true,
            };
            var s = await runner.Run();
            Console.WriteLine($"{k,4} {s.AvgOfBest,14:F2} {"±" + s.Ci95HalfWidth.ToString("F2"),10} {s.TotalRuns,10} {s.Elapsed.TotalSeconds,6:F1}");
        }
    }

    private static async Task DeckAvsB(IReadOnlyList<Type> deckA, string nameA, IReadOnlyList<Type> deckB, string nameB)
    {
        var policy = new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.30);
        const int seeds = 500;
        const int innerK = 50;

        var aRunner = new BestOfKRunner { DeckName = nameA, Deck = Harness.AsEntries(deckA), Policy = policy, Seeds = seeds, InnerSamples = innerK, Turns = 5 };
        var a = await aRunner.Run();
        var bRunner = new BestOfKRunner { DeckName = nameB, Deck = Harness.AsEntries(deckB), Policy = policy, Seeds = seeds, InnerSamples = innerK, Turns = 5 };
        var b = await bRunner.Run();

        // Two-sample diff with pooled std-err. Two independent groups of size N=Seeds.
        var lift = b.AvgOfBest - a.AvgOfBest;
        var diffStdErr = Math.Sqrt(a.StdErrOfMean * a.StdErrOfMean + b.StdErrOfMean * b.StdErrOfMean);
        var diffCi95 = 1.96 * diffStdErr;
        var z = lift / diffStdErr;

        Console.WriteLine();
        Console.WriteLine($"  Deck A — {nameA,-40}: {a.AvgOfBest,7:F2} ± {a.Ci95HalfWidth:F2}  ({a.AvgOfBest / 5:F2}/turn)");
        Console.WriteLine($"  Deck B — {nameB,-40}: {b.AvgOfBest,7:F2} ± {b.Ci95HalfWidth:F2}  ({b.AvgOfBest / 5:F2}/turn)");
        Console.WriteLine($"  Difference: {(lift >= 0 ? "+" : "")}{lift:F2} ± {diffCi95:F2} dmg over 5 turns  (z={z:F2})");
        var verdict = z switch
        {
            > 2.5 => "ADD IT (highly significant)",
            > 1.96 => "ADD IT (significant at 95%)",
            < -2.5 => "DO NOT ADD (highly significant)",
            < -1.96 => "DO NOT ADD (significant at 95%)",
            _ => "INCONCLUSIVE — run more seeds, or the swap genuinely doesn't matter",
        };
        Console.WriteLine($"  → Verdict: {verdict}");
    }

    private static async Task CompareDeck(string deckName, IReadOnlyList<Type> deck)
    {
        Console.WriteLine($"\n========== {deckName} ==========");

        var basePolicies = new IPlayPolicy[]
        {
            new HighestDamagePolicy(),
            new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.05),
            new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.10),
            new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.20),
            new EpsilonGreedyPolicy(new HighestDamagePolicy(), 0.50),
            new RandomPolicy(),
        };

        const int seeds = 200;
        const int innerK = 30;

        var results = new List<BestOfKRunner.Summary>();
        foreach (var policy in basePolicies)
        {
            var runner = new BestOfKRunner
            {
                DeckName = deckName,
                Deck = Harness.AsEntries(deck),
                Policy = policy,
                Turns = 5,
                Seeds = seeds,
                InnerSamples = innerK,
                ProgressInterval = TimeSpan.FromSeconds(5),
            };
            results.Add(await runner.Run());
        }

        Console.WriteLine();
        Console.WriteLine($"--- Per-policy summary on {deckName} (seeds={seeds}, K={innerK}) ---");
        Console.WriteLine($"{"Policy",-32} {"avg-of-best",14} {"per turn",10} {"runs",10}");
        foreach (var s in results)
        {
            Console.WriteLine($"{s.PolicyName,-32} {s.AvgOfBest,14:F2} {s.AvgOfBest / 5,10:F2} {s.TotalRuns,10}");
        }
        var bestPolicy = results.OrderByDescending(s => s.AvgOfBest).First();
        var greedyResult = results.First(s => s.PolicyName == "highest-damage");
        var lift = bestPolicy.AvgOfBest - greedyResult.AvgOfBest;
        Console.WriteLine();
        Console.WriteLine($"  Winner: {bestPolicy.PolicyName} ({bestPolicy.AvgOfBest:F2})");
        Console.WriteLine($"  Lift over pure greedy: +{lift:F2} ({lift / greedyResult.AvgOfBest * 100:F1}%)");
    }
}
