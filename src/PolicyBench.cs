using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models.Cards;

namespace StS2Sim;

/// <summary>
/// Policy uplift benchmark: a pinned suite of decks × encounters, each run
/// with the old single race policy and with the threshold portfolio, on the
/// same seeds. Because the play policy is a LOWER BOUND on true winnability,
/// any policy change that raises win rate / score on the same seeds is
/// strictly more accurate — there is no overfitting direction. Run after any
/// policy change: <c>dotnet run -c Release -- policy-bench</c>.
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
        Console.WriteLine("=== StS2 Policy Bench — race-only vs threshold portfolio ===");
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
            // The portfolio hypothesis test: a deck whose payoff is on turn 8+,
            // so surviving the early turns should actually convert into wins.
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

        double totalRaceScore = 0, totalPortfolioScore = 0;
        int totalRaceWins = 0, totalPortfolioWins = 0, totalSeeds = 0;
        var mixTotals = new Dictionary<string, int>();

        Console.WriteLine($"  {"deck",-12} {"encounter",-26} {"race win%/score",18} {"portfolio win%/score",21}  winning lines");
        foreach (var deck in decks)
        {
            foreach (var enc in encounters)
            {
                var race = await RunOne(deck, enc.Id, portfolio: false);
                var port = await RunOne(deck, enc.Id, portfolio: true);

                totalRaceScore += race.AvgOfBest;
                totalPortfolioScore += port.AvgOfBest;
                totalRaceWins += race.WinnableSeeds ?? 0;
                totalPortfolioWins += port.WinnableSeeds ?? 0;
                totalSeeds += BenchSeeds;
                if (port.PersonalityWins != null)
                    foreach (var (k, v) in port.PersonalityWins)
                        mixTotals[Short(k)] = mixTotals.GetValueOrDefault(Short(k)) + v;

                var mix = port.PersonalityWins == null ? "" : string.Join(" ", port.PersonalityWins
                    .OrderByDescending(kv => kv.Value)
                    .Select(kv => $"{Short(kv.Key)}:{kv.Value}"));
                Console.WriteLine(
                    $"  {deck.Name,-12} {enc.Name,-26} {Pct(race.WinnableSeeds),5} {race.AvgOfBest,10:F1}  {Pct(port.WinnableSeeds),5} {port.AvgOfBest,13:F1}  {mix}");
            }
        }
        sw.Stop();

        Console.WriteLine();
        Console.WriteLine($"  AGGREGATE  win rate: race {100.0 * totalRaceWins / totalSeeds:F1}% → portfolio {100.0 * totalPortfolioWins / totalSeeds:F1}%" +
                          $"   avg score: {totalRaceScore / (decks.Length * encounters.Count):F1} → {totalPortfolioScore / (decks.Length * encounters.Count):F1}");
        Console.WriteLine($"  PERSONALITY MIX (seeds won across suite): {string.Join(" · ", mixTotals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
        Console.WriteLine($"  Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    private static string Pct(int? wins)
        => wins == null ? "—" : $"{100.0 * wins.Value / BenchSeeds:F0}%";

    private static string Short(string name)
    {
        var idx = name.LastIndexOf('-');
        return idx >= 0 ? name[(idx + 1)..] : name;
    }

    private static async Task<BestOfKRunner.Summary> RunOne(DeckSpec deck, string encounterId, bool portfolio)
    {
        var runner = new BestOfKRunner
        {
            DeckName = deck.Name,
            Deck = deck.Deck,
            EncounterId = encounterId,
            Policy = new EpsilonGreedyPolicy(new ThresholdPolicy(-1), BenchEpsilon),
            // Weighted allocation: race keeps half the K budget (it wins the
            // most seeds), the threshold personalities share the insurance
            // half. The equal split measurably cost more in lost race samples
            // than it gained in defensive coverage.
            Portfolio = !portfolio ? null : new IPlayPolicy[]
            {
                new EpsilonGreedyPolicy(new ThresholdPolicy(-1), BenchEpsilon),
                new EpsilonGreedyPolicy(new ThresholdPolicy(0.15), BenchEpsilon),
                new EpsilonGreedyPolicy(new ThresholdPolicy(-1), BenchEpsilon),
                new EpsilonGreedyPolicy(new ThresholdPolicy(0.50), BenchEpsilon),
                new EpsilonGreedyPolicy(new ThresholdPolicy(-1), BenchEpsilon),
                new EpsilonGreedyPolicy(new ThresholdPolicy(double.PositiveInfinity), BenchEpsilon),
            },
            Seeds = BenchSeeds,
            InnerSamples = BenchK,
            Patience = BenchPatience,
            Turns = BenchMaxTurns,
            Quiet = true,
        };
        return await runner.Run();
    }
}
