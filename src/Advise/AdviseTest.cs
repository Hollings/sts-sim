using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace StS2Sim.Advise;

/// <summary>
/// Smoke test for the combat advisor (<c>dotnet run -- advise-test</c>):
/// builds a synthetic mid-combat state (Ironclad starter hand vs the weak
/// slimes lineup), runs the full mirror → rollout → rank pipeline twice —
/// once with rolled intents, once with a real forced intent discovered from
/// the first mirror — and asserts the output is sane. Exit 2 on failure.
/// </summary>
internal static class AdviseTest
{
    public static async Task<int> RunAll()
    {
        Harness.Bootstrap();
        Console.WriteLine("=== StS2 Combat Advisor Smoke Test ===\n");
        var failures = new List<string>();

        var req = BuildSyntheticState(intent: null);

        // ── Pass 1: rolled intents (no 'intent' field in the request).
        var advice = await CombatAdvisor.Advise(req, seeds: 8, horizon: 8);
        PrintAdvice("Pass 1 — rolled intents", advice);
        Check(failures, advice.Actions.Count >= 4, "expected >= 4 ranked actions (3 distinct plays x targets + end_turn)");
        Check(failures, advice.Actions.Any(a => a.Type == "end_turn"), "end_turn missing from ranking");
        Check(failures, advice.Actions[0].Type != "end_turn", "end_turn ranked above playing cards in a winnable fight");
        Check(failures, advice.Actions.All(a => double.IsFinite(a.AvgScore)), "non-finite score");
        Check(failures, advice.Actions.All(a => a.Rollouts > 0), "action with zero completed rollouts");
        Check(failures, advice.DrawInferred == advice.DrawReported,
            $"draw inference mismatch on a clean state ({advice.DrawInferred} vs {advice.DrawReported})");

        // ── Pass 2: discover a real move id from a throwaway mirror, then
        // force it — exercises SetMoveImmediate end to end.
        string? realIntent = null;
        var probe = await StateMirror.Mirror(req, seed: 1);
        try
        {
            realIntent = probe.H.Enemies[0].Monster?.NextMove?.Id;
        }
        finally
        {
            Harness.EndCombat();
        }
        Check(failures, !string.IsNullOrEmpty(realIntent), "could not discover a move id from the probe mirror");

        if (!string.IsNullOrEmpty(realIntent))
        {
            var req2 = BuildSyntheticState(intent: realIntent);
            var advice2 = await CombatAdvisor.Advise(req2, seeds: 8, horizon: 8);
            PrintAdvice($"Pass 2 — forced intent '{realIntent}'", advice2);
            Check(failures, !advice2.Notes.Any(n => n.Contains("not found in move state machine")),
                "forced intent fell back to a roll: " + string.Join("; ", advice2.Notes));
            Check(failures, advice2.Actions.Count >= 4, "pass 2: expected >= 4 ranked actions");
        }

        Console.WriteLine();
        if (failures.Count == 0)
        {
            Console.WriteLine("  All advisor checks pass.");
            return 0;
        }
        Console.WriteLine($"  {failures.Count} FAILURE(S):");
        foreach (var f in failures) Console.WriteLine("    - " + f);
        return 2;
    }

    /// <summary>Ironclad turn 1: full starter deck, 5-card hand, vs the weak
    /// leaf slimes — small enough that rollouts win, big enough to rank.</summary>
    private static AdviseRequest BuildSyntheticState(string? intent) => new()
    {
        Run = new() { Character = "CHARACTER.IRONCLAD" },
        Player = new() { Hp = 64, MaxHp = 80, Block = 0 },
        Relics = new() { new() { Id = "RELIC.BURNING_BLOOD" } },
        Deck = new[]
            {
                ("CARD.STRIKE_IRONCLAD", 5), ("CARD.DEFEND_IRONCLAD", 4), ("CARD.BASH", 1),
            }
            .SelectMany(p => Enumerable.Repeat(p.Item1, p.Item2))
            .Select(id => new AdviseRequest.CardRef { Id = id, Upgraded = false })
            .ToList(),
        Combat = new()
        {
            Turn = 1,
            Energy = 3,
            DrawPileCount = 5,
            Hand = new()
            {
                new() { HandIndex = 0, Uid = "strike_1", Id = "CARD.STRIKE_IRONCLAD", Playable = true, TargetType = "AnyEnemy" },
                new() { HandIndex = 1, Uid = "strike_2", Id = "CARD.STRIKE_IRONCLAD", Playable = true, TargetType = "AnyEnemy" },
                new() { HandIndex = 2, Uid = "bash_1", Id = "CARD.BASH", Playable = true, TargetType = "AnyEnemy" },
                new() { HandIndex = 3, Uid = "defend_1", Id = "CARD.DEFEND_IRONCLAD", Playable = true, TargetType = "Self" },
                new() { HandIndex = 4, Uid = "defend_2", Id = "CARD.DEFEND_IRONCLAD", Playable = true, TargetType = "Self" },
            },
            Enemies = new()
            {
                new() { EnemyIndex = 0, Id = "MONSTER.LEAF_SLIME_M", Hp = 32, MaxHp = 38, Block = 0, IsAlive = true, Intent = intent },
                new() { EnemyIndex = 1, Id = "MONSTER.LEAF_SLIME_S", Hp = 12, MaxHp = 12, Block = 0, IsAlive = true },
            },
        },
    };

    private static void PrintAdvice(string title, CombatAdvisor.Advice advice)
    {
        Console.WriteLine($"--- {title} ({advice.ElapsedSec:F1}s) ---");
        foreach (var a in advice.Actions)
            Console.WriteLine($"  {a.AvgScore,8:F1}  win {a.WinRate,5:P0}  {a.Label,-28} ({a.Rollouts} rollouts)");
        foreach (var n in advice.Notes)
            Console.WriteLine($"  note: {n}");
        Console.WriteLine();
    }

    private static void Check(List<string> failures, bool ok, string what)
    {
        if (!ok) failures.Add(what);
    }
}
