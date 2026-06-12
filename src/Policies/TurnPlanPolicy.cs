using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Localization.DynamicVars;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace StS2Sim;

/// <summary>
/// The "planner" brain: instead of greedily playing the highest-damage card,
/// it enumerates whole-turn play SETS over the current hand and picks the
/// best plan under a static damage model. This solves what the racer
/// structurally can't:
///
///  - the energy knapsack (2⚡ + [Bash, Strike, Strike]: two Strikes deal 12,
///    Bash deals 8 — greedy picks Bash),
///  - buff/debuff ordering (Inflame BEFORE Strikes, Bash's Vulnerable before
///    the follow-up hits),
///  - focus-fire target assignment (all planned damage onto the enemy it can
///    actually convert, via <see cref="ITargetingPolicy"/>).
///
/// The damage model is static estimation (base damage + Strength per hit,
/// ×1.5 Vulnerable, hit counts from RepeatVar), NOT a rollout — cards whose
/// value the model can't see (draw, block, X-cost scaling) estimate at their
/// base value and are played with leftover energy via the race fallback.
/// Still racing-shaped: the score is damage only; no defensive preference
/// (that hypothesis is falsified — see policy-bench).
///
/// Stateful per turn (it commits to a plan); resets whenever the combat
/// state or round changes, so one instance is safe across trials.
/// </summary>
internal sealed class TurnPlanPolicy : IPlayPolicy, ITargetingPolicy
{
    public string Name => "planner";

    private const int MaxSubsetEvaluations = 4096;
    private const decimal VulnerableMultiplier = 1.5m;

    private readonly HighestDamagePolicy _fallback = new();

    private CombatState? _plannedState;
    private int _plannedRound = -1;
    private readonly List<CardModel> _plan = new();
    private readonly Dictionary<CardModel, Creature> _targets = new();

    public CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng)
    {
        // New combat or new turn → stale plan.
        if (!ReferenceEquals(_plannedState, h.State) || _plannedRound != h.State.RoundNumber)
            ClearPlan(h);

        // Cards a hook vetoed (CanPlay false) THIS call. Without this set,
        // a replan re-emits the vetoed card (it's still in hand) and the
        // loop never terminates — boss play-limiting hooks (Ceremonial
        // Beast) spun the first bench run forever.
        var vetoed = new HashSet<CardModel>();

        while (true)
        {
            // Drop plan entries that left the hand (discarded, transformed),
            // became unaffordable (cost-raising effects), or were vetoed.
            var hand = h.Player.PlayerCombatState!.Hand.Cards;
            while (_plan.Count > 0 &&
                   (!hand.Contains(_plan[0])
                    || _plan[0].EnergyCost.GetAmountToSpend() > energyLeft
                    || vetoed.Contains(_plan[0])))
                _plan.RemoveAt(0);

            if (_plan.Count == 0)
            {
                ComputePlan(h, energyLeft, vetoed);
                if (_plan.Count == 0)
                {
                    // No damage plan left — spend remaining energy like the
                    // racer (skills, draws). A draw can add attacks to hand;
                    // the next call replans over them. The fallback's
                    // ChooseFrom CanPlay-screens its own pick, so vetoed
                    // cards can't loop through here either.
                    return _fallback.ChooseCard(h, energyLeft, rng);
                }
            }

            var pick = _plan[0];
            _plan.RemoveAt(0);
            if (pick.CanPlay()) return pick;
            vetoed.Add(pick);
        }
    }

    public Creature? ChooseTarget(Harness.CombatHarness h, CardModel card)
        => _targets.TryGetValue(card, out var t) && t.IsAlive ? t : null;

    private void ClearPlan(Harness.CombatHarness h)
    {
        _plan.Clear();
        _targets.Clear();
        _plannedState = h.State;
        _plannedRound = h.State.RoundNumber;
    }

    // ─── Planning ────────────────────────────────────────────────────────────

    private sealed record Spec(
        CardModel Card, int Cost, decimal Damage, int Hits,
        bool AppliesVuln, decimal StrengthGrant, bool IsAoe);

    private void ComputePlan(Harness.CombatHarness h, int energy, HashSet<CardModel>? exclude = null)
    {
        _plan.Clear();
        _targets.Clear();

        var specs = Playable.InHand(h, energy)
            .Where(c => exclude == null || !exclude.Contains(c))
            .Select(Describe)
            .Where(s => s.Damage > 0 || s.StrengthGrant > 0 || s.AppliesVuln)
            .ToList();
        if (specs.Count == 0) return;

        var enemies = h.State.Enemies
            .Where(e => e.IsAlive)
            .Select(e => (Hp: (decimal)(e.CurrentHp + e.Block), Vuln: e.HasPower<VulnerablePower>(), Creature: e))
            .ToList();
        if (enemies.Count == 0) return;

        decimal playerStrength = h.Player.Creature.GetPowerAmount<StrengthPower>();

        // Duplicate cards are interchangeable — branch over distinct groups
        // with counts, not individual copies.
        var groups = specs
            .GroupBy(s => (s.Card.Id.ToString(), s.Card.CurrentUpgradeLevel, s.Cost, s.Damage, s.Hits, s.AppliesVuln, s.StrengthGrant, s.IsAoe))
            .Select(g => g.ToList())
            .ToList();

        List<Spec>? bestSubset = null;
        int bestFocus = 0;
        decimal bestScore = -1;
        int bestLeftover = -1;
        int evaluations = 0;

        var current = new List<Spec>();
        void Dfs(int groupIdx, int energyLeft)
        {
            if (evaluations >= MaxSubsetEvaluations) return;
            if (groupIdx == groups.Count)
            {
                evaluations++;
                for (int f = 0; f < enemies.Count; f++)
                {
                    var score = Evaluate(current, enemies, f, playerStrength);
                    if (score > bestScore || (score == bestScore && energyLeft > bestLeftover))
                    {
                        bestScore = score;
                        bestLeftover = energyLeft;
                        bestSubset = current.ToList();
                        bestFocus = f;
                    }
                }
                return;
            }
            var group = groups[groupIdx];
            int cost = Math.Max(0, group[0].Cost);
            int maxTake = cost == 0 ? group.Count : Math.Min(group.Count, energyLeft / Math.Max(1, cost));
            for (int take = maxTake; take >= 0; take--)
            {
                current.AddRange(group.Take(take));
                Dfs(groupIdx + 1, energyLeft - take * cost);
                current.RemoveRange(current.Count - take, take);
                if (evaluations >= MaxSubsetEvaluations) return;
            }
        }
        Dfs(0, energy);

        if (bestSubset == null || bestScore <= 0) return;

        // Emit the plan in value order: Strength buffs → Vulnerable appliers
        // → remaining attacks (heaviest per-hit first; ordering among plain
        // attacks only matters for kill timing, which the clamp already priced).
        var focusCreature = enemies[bestFocus].Creature;
        foreach (var s in bestSubset.OrderBy(PlayPhase).ThenByDescending(s => s.Damage))
        {
            _plan.Add(s.Card);
            if (s.Card.TargetType == TargetType.AnyEnemy)
                _targets[s.Card] = focusCreature;
        }

        static int PlayPhase(Spec s) =>
            s.StrengthGrant > 0 && s.Damage == 0 ? 0
            : s.AppliesVuln ? 1
            : 2;
    }

    /// <summary>
    /// Score a play set against a chosen focus enemy: total EFFECTIVE damage
    /// (clamped per enemy — overkill is worthless), with Strength added per
    /// hit and ×1.5 once Vulnerable is up. AoE cards hit everyone; single-
    /// target damage all lands on the focus.
    /// </summary>
    private static decimal Evaluate(
        List<Spec> subset,
        List<(decimal Hp, bool Vuln, Creature Creature)> enemies,
        int focusIdx,
        decimal baseStrength)
    {
        decimal strength = baseStrength + subset.Sum(s => s.StrengthGrant);
        bool focusVuln = enemies[focusIdx].Vuln;
        var remaining = enemies.Select(e => e.Hp).ToList();
        decimal total = 0;

        decimal HitFor(Spec s, bool vuln)
        {
            var perHit = Math.Max(0, s.Damage + strength);
            if (vuln) perHit = Math.Floor(perHit * VulnerableMultiplier);
            return perHit * Math.Max(1, s.Hits);
        }

        void Land(int enemyIdx, decimal dmg)
        {
            var dealt = Math.Min(remaining[enemyIdx], dmg);
            remaining[enemyIdx] -= dealt;
            total += dealt;
        }

        // Vulnerable appliers swing first (their own hit lands pre-Vulnerable
        // unless the enemy already had it), then everything else enjoys it.
        foreach (var s in subset.Where(s => s.AppliesVuln && s.Damage > 0))
        {
            if (s.IsAoe)
                for (int i = 0; i < remaining.Count; i++) Land(i, HitFor(s, enemies[i].Vuln));
            else
                Land(focusIdx, HitFor(s, focusVuln));
            focusVuln = true;
        }
        if (subset.Any(s => s.AppliesVuln)) focusVuln = true;

        foreach (var s in subset.Where(s => !s.AppliesVuln && s.Damage > 0))
        {
            if (s.IsAoe)
                for (int i = 0; i < remaining.Count; i++)
                    Land(i, HitFor(s, i == focusIdx ? focusVuln : enemies[i].Vuln));
            else
                Land(focusIdx, HitFor(s, focusVuln));
        }
        return total;
    }

    private static Spec Describe(CardModel card)
    {
        decimal damage = 0;
        int hits = 1;
        bool vuln = false;
        decimal strength = 0;

        if (card.DynamicVars.TryGetValue("Damage", out var dmgVar) && card.Type == CardType.Attack)
            damage = ((DamageVar)dmgVar).BaseValue;
        if (card.DynamicVars.TryGetValue("Repeat", out var repVar))
            hits = Math.Max(1, ((RepeatVar)repVar).IntValue);
        if (card.DynamicVars.TryGetValue("VulnerablePower", out var vVar))
            vuln = vVar.BaseValue > 0;
        if (card.DynamicVars.TryGetValue("StrengthPower", out var sVar) && card.TargetType == TargetType.Self)
            strength = sVar.BaseValue;

        return new Spec(
            card,
            Math.Max(0, card.EnergyCost.GetAmountToSpend()),
            damage, hits, vuln, strength,
            card.TargetType == TargetType.AllEnemies);
    }
}
