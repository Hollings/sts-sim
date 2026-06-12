using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim;

/// <summary>
/// Policy uplift benchmark: a pinned suite of decks × encounters, each run
/// with the race brain and with the challenger (currently the turn-search
/// planner), on the same seeds. Because the play policy is a LOWER BOUND on
/// true winnability, any brain that raises win rate / score on the same seeds
/// is strictly more accurate — there is no overfitting direction. Run after
/// any policy change: <c>dotnet run -c Release -- policy-bench</c>.
///
/// History: the threshold/turtle PERSONALITY portfolio was benchmarked here
/// and falsified (racing dominates; splitting the K budget lowered win rates
/// — see CLAUDE.md "ruled out"). The planner is a different axis: same racing
/// objective, deeper SEARCH.
/// </summary>
internal static class PolicyBench
{
    private const int BenchSeeds = 80;
    private const int BenchK = 24;
    private const int BenchPatience = 12;
    private const int BenchMaxTurns = 25;
    private const double BenchEpsilon = 0.20;

    private sealed record DeckSpec(string Name, IReadOnlyList<Harness.DeckEntry> Deck);

    public static async Task<int> RunAll()
    {
        Console.WriteLine("=== StS2 Policy Bench — race brain vs turn-search planner ===");
        Console.WriteLine($"    seeds={BenchSeeds} K={BenchK} patience={BenchPatience} cap={BenchMaxTurns}t eps={BenchEpsilon}\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var decks = new[]
        {
            new DeckSpec("starter", Harness.AsEntries(new List<Type>
            {
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(Bash),
            })),
            new DeckSpec("aggro", Harness.AsEntries(new List<Type>
            {
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(TwinStrike), typeof(PommelStrike), typeof(Bludgeon),
                typeof(Inflame), typeof(Bloodletting),
                typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(Bash),
            })),
            new DeckSpec("block-heavy", Harness.AsEntries(new List<Type>
            {
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(StrikeIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(Bash),
            })),
            // Scaling payoff arrives turn 8+ — the planner's Inflame-first
            // ordering should shine here if anywhere.
            new DeckSpec("scaling", Harness.AsEntries(new List<Type>
            {
                typeof(Inflame), typeof(Inflame), typeof(DemonForm),
                typeof(StrikeIronclad), typeof(StrikeIronclad), typeof(StrikeIronclad),
                typeof(StrikeIronclad),
                typeof(DefendIronclad), typeof(DefendIronclad), typeof(DefendIronclad),
                typeof(Bash),
            })),
        };

        // Resolved by substring so a renamed encounter degrades to a skip, not a crash.
        var wantedEncounters = new[] { "VANTOM", "CEREMONIAL_BEAST", "BYRDONIS", "SLIMES_NORMAL" };
        var catalog = EncounterCatalog.GetEncounters();
        var encounters = wantedEncounters
            .Select(w => catalog.FirstOrDefault(e => e.Id.Contains(w)))
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();

        double totalRaceScore = 0, totalPlannerScore = 0;
        int totalRaceWins = 0, totalPlannerWins = 0, totalSeeds = 0;

        Console.WriteLine($"  {"deck",-12} {"encounter",-26} {"race win%/score",18} {"planner win%/score",21}");
        foreach (var deck in decks)
        {
            foreach (var enc in encounters)
            {
                // Both ε-wrapped: a deterministic planner can't harvest the K
                // budget (all samples identical) — that variant lost the
                // bench by construction, not by play quality.
                var race = await RunOne(deck, enc.Id, () => new EpsilonGreedyPolicy(new HighestDamagePolicy(), BenchEpsilon));
                var plan = await RunOne(deck, enc.Id, () => new EpsilonGreedyPolicy(new TurnPlanPolicy(), BenchEpsilon));

                totalRaceScore += race.AvgOfBest;
                totalPlannerScore += plan.AvgOfBest;
                totalRaceWins += race.WinnableSeeds ?? 0;
                totalPlannerWins += plan.WinnableSeeds ?? 0;
                totalSeeds += BenchSeeds;

                Console.WriteLine(
                    $"  {deck.Name,-12} {enc.Name,-26} {Pct(race.WinnableSeeds),5} {race.AvgOfBest,10:F1}  {Pct(plan.WinnableSeeds),5} {plan.AvgOfBest,13:F1}");
            }
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  AGGREGATE  win rate: race {100.0 * totalRaceWins / totalSeeds:F1}% → planner {100.0 * totalPlannerWins / totalSeeds:F1}%" +
                          $"   avg score: {totalRaceScore / (decks.Length * encounters.Count):F1} → {totalPlannerScore / (decks.Length * encounters.Count):F1}");
        Console.WriteLine($"  Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static string Pct(int? wins)
        => wins == null ? "—" : $"{100.0 * wins.Value / BenchSeeds:F0}%";

    private static async Task<BestOfKRunner.Summary> RunOne(DeckSpec deck, string encounterId, Func<IPlayPolicy> makePolicy)
    {
        var runner = new BestOfKRunner
        {
            DeckName = deck.Name,
            Deck = deck.Deck,
            EncounterId = encounterId,
            Policy = makePolicy(),
            Seeds = BenchSeeds,
            InnerSamples = BenchK,
            Patience = BenchPatience,
            Turns = BenchMaxTurns,
            Quiet = true,
        };
        return await runner.Run();
    }
}
