using System;
using System.Linq;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.ValueProps;

namespace StS2Sim;

/// <summary>
/// Reads enemy intents the same way the in-game intent UI does. Moves are
/// rolled at player-turn start (TurnHooks.RollEnemyMoves), exactly like the
/// real game, so during card selection every living enemy's NextMove is the
/// genuine telegraph for this round.
///
/// The game's own AttackIntent.GetSingleDamage resolves the player through
/// LocalContext (netcode) and silently falls back to BASE damage headless —
/// so we run Hook.ModifyDamage ourselves against the harness player, giving
/// strength/weak/vulnerable-adjusted numbers, same as the on-screen intent.
/// </summary>
internal static class IntentReader
{
    /// <summary>Total damage the player is telegraphed to take this round.</summary>
    public static int IncomingDamage(Harness.CombatHarness h)
    {
        int total = 0;
        foreach (var enemy in h.State.Enemies)
        {
            if (!enemy.IsAlive || enemy.Monster == null) continue;
            foreach (var intent in enemy.Monster.NextMove.Intents)
            {
                if (intent is not AttackIntent attack || attack.DamageCalc == null) continue;
                decimal perHit;
                try
                {
                    perHit = Hook.ModifyDamage(
                        h.Player.RunState, h.State, h.Player.Creature, enemy,
                        attack.DamageCalc(), ValueProp.Move, null,
                        ModifyDamageHookType.All, CardPreviewMode.None, out _);
                }
                catch
                {
                    perHit = attack.DamageCalc();
                }
                int hits = Math.Max(1, attack.Repeats);
                total += Math.Max(0, (int)perHit) * hits;
            }
        }
        return total;
    }
}
