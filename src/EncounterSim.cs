using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Characters;

namespace StS2Sim;

/// <summary>
/// One trial = a full simulated combat against a real encounter (boss, elite,
/// or normal fight): player turns alternate with genuine enemy turns driven by
/// each monster's MonsterMoveStateMachine, until one side dies or the turn cap
/// hits. Unlike <see cref="DamagePerTurnSim"/>, nothing is healed back —
/// survival is the point.
///
/// Scoring (the deck-quality scalar fed to best-of-K):
///   win  → +remaining player HP   ("won with 43 HP left")
///   loss → −remaining enemy HP    ("died with the boss at 91 HP")
///   cap  → counts as a loss (stalling out a boss is a loss in practice)
/// Monotone and continuous-ish across the win boundary, so best-of-K's max
/// and the paired A/B diffs both behave sensibly.
/// </summary>
internal sealed class EncounterSim
{
    /// <summary>Null = the character's real starter deck.</summary>
    public IReadOnlyList<Harness.DeckEntry>? Deck { get; init; }
    public required string EncounterId { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public Type CharacterType { get; init; } = typeof(Ironclad);
    public int HandSize { get; init; } = 5;
    /// <summary>Turn cap; reaching it without killing the encounter is a loss.</summary>
    public int MaxTurns { get; init; } = 30;
    public IPlayPolicy Policy { get; init; } = new HighestDamagePolicy();
    public uint? PolicyRngSeed { get; init; } = null;

    public sealed record TrialResult(
        uint Seed,
        bool Win,
        int TurnsTaken,
        int PlayerHpRemaining,
        int PlayerMaxHp,
        int EnemyHpRemaining,
        IReadOnlyList<DamagePerTurnSim.TurnResult> Turns)
    {
        /// <summary>The best-of-K scalar. Positive = win (HP kept), negative = loss (boss HP left).</summary>
        public int Score => Win ? PlayerHpRemaining : -EnemyHpRemaining;
    }

    public async Task<TrialResult> RunSingleTrial(uint shuffleSeed)
    {
        var harness = Harness.BeginCombat(
            CharacterType, deckOverride: Deck, shuffleSeed: shuffleSeed,
            relicIds: Relics, encounterId: EncounterId);
        var policyRng = new Random((int)(PolicyRngSeed ?? shuffleSeed ^ 0xDEAD_BEEFu));
        try
        {
            // At-combat-start effects, in game order: monster room-entry
            // effects (Vantom's Slippery, minion buffs) + initial intent roll,
            // then the player's relic hooks.
            foreach (var enemy in harness.Enemies)
            {
                if (enemy.Monster != null)
                    await enemy.Monster.AfterAddedToRoom();
            }
            TurnHooks.RollEnemyMoves(harness);
            await TurnHooks.FireAfterRoomEntered(harness);
            await TurnHooks.FireBeforeCombatStart(harness);

            var player = harness.Player.Creature;
            var turnResults = new List<DamagePerTurnSim.TurnResult>();
            bool win = false;
            int turn = 0;

            while (turn < MaxTurns)
            {
                turn++;
                await TurnHooks.PrepareSideTurnStart(harness, turn);
                TurnHooks.RollEnemyMoves(harness);

                var enemyHpBefore = TotalEnemyHp(harness);
                var events = new List<PlayCapture.Event>();
                PlayCapture.Start(events);

                await TurnHooks.PlayerTurnStartDraw(harness, HandSize);
                await DamagePerTurnSim.RunPlayPhase(
                    harness, Policy, policyRng,
                    chooseEnemyTarget: () => LowestHpAliveEnemy(harness),
                    stop: () => AllEnemiesDead(harness));

                if (AllEnemiesDead(harness))
                {
                    PlayCapture.Stop();
                    turnResults.Add(new DamagePerTurnSim.TurnResult(turn, enemyHpBefore - TotalEnemyHp(harness), events));
                    win = true;
                    break;
                }

                await TurnHooks.EndOfPlayerTurn(harness, tickEnemySide: false);

                // The real enemy turn: every living monster performs its
                // rolled move through the game's own pipeline.
                var playerHpBeforeEnemy = player.CurrentHp;
                await TurnHooks.EnemyTurn(harness, turn, (enemy, moveId) =>
                {
                    var dmg = playerHpBeforeEnemy - player.CurrentHp;
                    PlayCapture.RecordEnemyMove(
                        $"{CardLabels.PrettyName(enemy.Monster!.Id.ToString())} · {CardLabels.PrettyName("M." + moveId.Replace("_MOVE", ""))}",
                        dmg > 0 ? $"{dmg} dmg" : null);
                    playerHpBeforeEnemy = player.CurrentHp;
                });

                PlayCapture.Stop();
                turnResults.Add(new DamagePerTurnSim.TurnResult(turn, enemyHpBefore - TotalEnemyHp(harness), events));

                if (!player.IsAlive) break;
                if (AllEnemiesDead(harness)) { win = true; break; } // thorns/poison style deaths
            }

            return new TrialResult(
                shuffleSeed,
                Win: win,
                TurnsTaken: turn,
                PlayerHpRemaining: Math.Max(0, player.CurrentHp),
                PlayerMaxHp: player.MaxHp,
                EnemyHpRemaining: TotalEnemyHp(harness),
                Turns: turnResults);
        }
        finally
        {
            PlayCapture.Stop();
            Harness.EndCombat();
        }
    }

    // All of these read State.Enemies (not the harness's initial list) so
    // mid-combat spawns count, and the win condition uses the game's own
    // semantics: combat ends when no PRIMARY enemy lives (background or
    // utility creatures don't keep a fight alive). Internal: the combat
    // advisor's rollouts share these definitions.
    internal static bool AllEnemiesDead(Harness.CombatHarness h)
        => !h.State.Enemies.Any(e => e.IsAlive && e.IsPrimaryEnemy);

    internal static int TotalEnemyHp(Harness.CombatHarness h)
        => h.State.Enemies.Where(e => e.IsAlive && e.IsPrimaryEnemy).Sum(e => e.CurrentHp);

    /// <summary>
    /// Focus-fire heuristic: target the living enemy closest to death.
    /// Matches how humans usually secure kills; good enough until policies
    /// learn real targeting.
    /// </summary>
    internal static Creature? LowestHpAliveEnemy(Harness.CombatHarness h)
        => h.State.Enemies.Where(e => e.IsAlive).OrderBy(e => e.CurrentHp).FirstOrDefault();
}
