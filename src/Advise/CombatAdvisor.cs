using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim.Advise;

/// <summary>
/// "Which action do I take right now?" — the per-decision evaluator behind
/// <c>POST /api/advise/combat</c>. For every legal action in the live state
/// (each distinct playable card × target, plus end_turn), it mirrors the
/// combat <see cref="StateMirror"/>, performs that action first, finishes the
/// turn and the following turns with the race policy, and scores the outcome
/// with the same scalar fight sims use (win → +HP kept, loss/horizon →
/// −enemy HP). Averaged over K seeds (which sample the unknowns: draw order,
/// future enemy moves), the ranking is "expected outcome if I start with X".
///
/// One advise call per DECISION, not per turn — the driver plays the top
/// action, re-polls the live state, and asks again with one card fewer in
/// hand. That keeps the branching honest without tree search.
/// </summary>
internal static class CombatAdvisor
{
    public sealed record ActionAdvice(
        string Type,            // "play_card" | "end_turn"
        string? CardUid,        // echo of the request's uid (driver convenience)
        int? HandIndex,         // live hand index
        int? TargetIndex,       // live enemy_index (only for targeted plays)
        string Label,
        double AvgScore,
        double WinRate,
        int Rollouts);

    public sealed record Advice(
        IReadOnlyList<ActionAdvice> Actions,
        int DrawInferred,
        int DrawReported,
        IReadOnlyList<string> Notes,
        double ElapsedSec);

    private sealed record Candidate(
        bool IsEndTurn, string? CardId, bool Upgraded,
        string? Uid, int? HandIndex, int? TargetLiveIndex, string Label);

    public static async Task<Advice> Advise(AdviseRequest live, int seeds = 12, int horizon = 8)
    {
        var combat = live.Combat ?? throw new ArgumentException("request has no 'combat' block");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var candidates = BuildCandidates(combat);

        var results = new List<ActionAdvice>();
        IReadOnlyList<string> notes = Array.Empty<string>();
        int drawInferred = 0, drawReported = combat.DrawPileCount;

        foreach (var cand in candidates)
        {
            double scoreSum = 0;
            int wins = 0, n = 0;
            for (int k = 0; k < seeds; k++)
            {
                // Same seed sequence for every candidate → paired comparison:
                // candidate i and candidate j face identical draw orders and
                // identical future enemy rolls on sample k.
                uint seed = (uint)(0xAD715E0 + k * 7919);
                var (score, win, mirror) = await Rollout(live, cand, seed, horizon);
                if (mirror != null && k == 0 && results.Count == 0)
                {
                    notes = mirror.Notes;
                    drawInferred = mirror.DrawInferred;
                    drawReported = mirror.DrawReported;
                }
                if (score == null) continue; // mirror couldn't stage this candidate
                scoreSum += score.Value;
                if (win) wins++;
                n++;
            }
            if (n == 0) continue;
            results.Add(new ActionAdvice(
                cand.IsEndTurn ? "end_turn" : "play_card",
                cand.Uid, cand.HandIndex, cand.TargetLiveIndex, cand.Label,
                AvgScore: scoreSum / n,
                WinRate: (double)wins / n,
                Rollouts: n));
        }

        sw.Stop();
        return new Advice(
            results.OrderByDescending(r => r.AvgScore).ToList(),
            drawInferred, drawReported, notes, sw.Elapsed.TotalSeconds);
    }

    // ── Candidates ───────────────────────────────────────────────────────────

    /// <summary>
    /// One candidate per distinct (card id, upgrade, target). Duplicate hand
    /// copies collapse (two Strikes at the same target are the same decision);
    /// AnyEnemy cards fan out across living enemies; everything else self/auto
    /// targets. end_turn is always a candidate — "stop playing" is a real
    /// option the race policy never takes on its own.
    /// </summary>
    private static List<Candidate> BuildCandidates(AdviseRequest.CombatBlock combat)
    {
        var living = combat.Enemies.Where(e => e.IsAlive).ToList();
        var candidates = new List<Candidate>();
        var seen = new HashSet<(string, bool, int)>();

        foreach (var card in combat.Hand)
        {
            if (!card.Playable || string.IsNullOrEmpty(card.Id)) continue;
            var name = CardLabels.Format(card.Id, card.Upgraded ? 1 : 0);

            if (string.Equals(card.TargetType, "AnyEnemy", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var e in living)
                {
                    if (!seen.Add((card.Id, card.Upgraded, e.EnemyIndex))) continue;
                    var enemyName = CardLabels.PrettyName(e.Id ?? "?");
                    candidates.Add(new Candidate(false, card.Id, card.Upgraded,
                        card.Uid, card.HandIndex, e.EnemyIndex, $"{name} → {enemyName}"));
                }
            }
            else
            {
                if (!seen.Add((card.Id, card.Upgraded, -1))) continue;
                candidates.Add(new Candidate(false, card.Id, card.Upgraded,
                    card.Uid, card.HandIndex, null, name));
            }
        }

        candidates.Add(new Candidate(true, null, false, null, null, null, "end turn"));
        return candidates;
    }

    // ── Rollout ──────────────────────────────────────────────────────────────

    private static async Task<(double? Score, bool Win, StateMirror.MirrorResult? Mirror)> Rollout(
        AdviseRequest live, Candidate cand, uint seed, int horizon)
    {
        StateMirror.MirrorResult mirror;
        try
        {
            mirror = await StateMirror.Mirror(live, seed);
        }
        catch (Exception)
        {
            Harness.EndCombat();
            throw;
        }

        var h = mirror.H;
        try
        {
            var policy = new HighestDamagePolicy();
            var policyRng = new Random((int)(seed ^ 0xA15ED00D));
            var player = h.Player.Creature;
            int round = h.State.RoundNumber;

            GodotShims.EndTurnRequested = false;
            if (!cand.IsEndTurn)
            {
                var card = FindHandCard(h, cand);
                if (card == null) return (null, false, mirror); // placement failed; skip sample
                var target = ResolveTarget(h, mirror, cand, card);
                if (target == null) return (null, false, mirror);
                await PlayCard(h, card, target);
            }

            // Finish the current turn with the race policy (unless the
            // candidate itself ended the turn, e.g. Void Form).
            if (!cand.IsEndTurn && !GodotShims.EndTurnRequested && !EncounterSim.AllEnemiesDead(h) && player.IsAlive)
            {
                await DamagePerTurnSim.RunPlayPhase(
                    h, policy, policyRng,
                    chooseEnemyTarget: () => EncounterSim.LowestHpAliveEnemy(h),
                    stop: () => EncounterSim.AllEnemiesDead(h));
            }

            bool win = EncounterSim.AllEnemiesDead(h);

            // Alternate full turns exactly like EncounterSim until the fight
            // resolves or the horizon treats it as a stall-out loss.
            int turn = round;
            while (!win && player.IsAlive && turn < round + horizon)
            {
                await TurnHooks.EndOfPlayerTurn(h, tickEnemySide: false);
                await TurnHooks.EnemyTurn(h, turn);
                if (!player.IsAlive) break;
                if (EncounterSim.AllEnemiesDead(h)) { win = true; break; }

                turn++;
                await TurnHooks.PrepareSideTurnStart(h, turn);
                TurnHooks.RollEnemyMoves(h);
                await TurnHooks.PlayerTurnStartDraw(h, 5);
                await DamagePerTurnSim.RunPlayPhase(
                    h, policy, policyRng,
                    chooseEnemyTarget: () => EncounterSim.LowestHpAliveEnemy(h),
                    stop: () => EncounterSim.AllEnemiesDead(h));
                if (EncounterSim.AllEnemiesDead(h)) win = true;
            }

            double score = win
                ? Math.Max(0, player.CurrentHp)
                : -EncounterSim.TotalEnemyHp(h);
            return (score, win, mirror);
        }
        finally
        {
            Harness.EndCombat();
        }
    }

    private static CardModel? FindHandCard(Harness.CombatHarness h, Candidate cand)
    {
        var hand = h.Player.PlayerCombatState!.Hand.Cards;
        int wantLevel = cand.Upgraded ? 1 : 0;
        return hand.FirstOrDefault(c => c.Id.ToString() == cand.CardId && c.CurrentUpgradeLevel == wantLevel)
            ?? hand.FirstOrDefault(c => c.Id.ToString() == cand.CardId);
    }

    private static Creature? ResolveTarget(
        Harness.CombatHarness h, StateMirror.MirrorResult mirror, Candidate cand, CardModel card)
    {
        if (card.TargetType == TargetType.Self || cand.TargetLiveIndex == null)
            return card.TargetType == TargetType.Self ? h.Player.Creature
                : EncounterSim.LowestHpAliveEnemy(h) ?? h.Player.Creature;

        var mirrorIdx = ((List<int>)mirror.LiveEnemyIndexByMirrorIndex).IndexOf(cand.TargetLiveIndex.Value);
        if (mirrorIdx >= 0 && mirrorIdx < h.Enemies.Count && h.Enemies[mirrorIdx].IsAlive)
            return h.Enemies[mirrorIdx];
        return EncounterSim.LowestHpAliveEnemy(h);
    }

    /// <summary>The game's own resource debit + play wrapper (same path the
    /// sims use: X-cost capture, star costs, AfterEnergySpent hooks).</summary>
    private static async Task PlayCard(Harness.CombatHarness h, CardModel card, Creature target)
    {
        var (energySpent, starsSpent) = await card.SpendResources();
        var resources = new ResourceInfo
        {
            EnergySpent = energySpent,
            EnergyValue = energySpent,
            StarsSpent = starsSpent,
            StarValue = starsSpent,
        };
        await card.OnPlayWrapper(h.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
    }
}
