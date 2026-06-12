using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Boss-quirk integration tests, driven through the same harness calls
/// EncounterSim makes. The archetype: Test Subject is a THREE-phase boss —
/// its AdaptablePower prevents combat from ending on death
/// (ShouldStopCombatFromEnding), keeps the corpse in the combat state, and
/// schedules a respawn move via the AfterDeath hook. A sim that only checks
/// "no living enemies" calls phase 1 a win.
/// </summary>
internal static class BossMechanicsTests
{
    public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
    {
        var results = new List<TestHelpers.TestResult>
        {
            await TestSubjectPhases(),
            await WaterfallGiantEruption(),
        };
        return results;
    }

    /// <summary>
    /// Waterfall Giant: its moves stack SteamEruptionPower; killing it while
    /// any stacks remain revives it unkillable (999,999,999 HP), it telegraphs
    /// ABOUT_TO_BLOW, then EXPLODEs for the banked stacks and kills itself.
    /// "Winning" means surviving the eruption — a sim that scores the first
    /// kill as a win overstates the deck's HP-kept by the whole eruption hit.
    /// </summary>
    private static async Task<TestHelpers.TestResult> WaterfallGiantEruption()
    {
        const string name = "Waterfall Giant revives and explodes before the win counts";
        Harness.CombatHarness? h = null;
        try
        {
            h = Harness.BeginCombat<Ironclad>(
                new List<Harness.DeckEntry> { new(typeof(StrikeIronclad)) },
                shuffleSeed: 7,
                encounterId: "ENCOUNTER.WATERFALL_GIANT_BOSS");

            foreach (var enemy in h.Enemies)
            {
                if (enemy.Monster != null)
                    await enemy.Monster.AfterAddedToRoom();
            }
            TurnHooks.RollEnemyMoves(h);
            var boss = h.Enemies.Single();

            // Let it act once so it pressurizes (first move applies steam stacks).
            await TurnHooks.EnemyTurn(h, 1);
            var f = TestHelpers.ExpectPower(boss, "STEAM_ERUPTION");
            if (f != null) return Fail(name, f);

            // ── Kill with stacks banked: revives unkillable, blow telegraphed.
            await CreatureCmd.Kill(boss);
            if (EncounterSim.AllEnemiesDead(h))
                return Fail(name, "kill with steam stacks counted as combat won");
            if (!boss.IsAlive)
                return Fail(name, "boss did not revive into the eruption phase (AfterDeath hook didn't fire?)");
            if (boss.Monster!.NextMove.Id != "ABOUT_TO_BLOW_MOVE")
                return Fail(name, $"expected ABOUT_TO_BLOW_MOVE, got {boss.Monster.NextMove.Id}");

            // ── Telegraph turn, then the eruption: damages the player and
            // kills itself — combat is finally over. RollEnemyMoves between
            // turns mirrors EncounterSim's loop (it's what advances the move
            // chain past a performed state).
            var hpBefore = h.Player.Creature.CurrentHp;
            await TurnHooks.EnemyTurn(h, 2); // ABOUT_TO_BLOW (banks stacks)
            if (EncounterSim.AllEnemiesDead(h)) return Fail(name, "combat ended during the telegraph turn");
            TurnHooks.RollEnemyMoves(h);
            await TurnHooks.EnemyTurn(h, 3); // EXPLODE
            if (!EncounterSim.AllEnemiesDead(h))
                return Fail(name, "combat not won after the eruption resolved");
            if (h.Player.Creature.CurrentHp >= hpBefore)
                return Fail(name, "eruption dealt no damage to the player");

            return new TestHelpers.TestResult(name, TestHelpers.Outcome.Pass);
        }
        catch (System.Exception ex)
        {
            return new TestHelpers.TestResult(name, TestHelpers.Outcome.Crash,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != null) Harness.EndCombat();
        }
    }

    private static async Task<TestHelpers.TestResult> TestSubjectPhases()
    {
        const string name = "Test Subject runs all three phases before the win counts";
        Harness.CombatHarness? h = null;
        try
        {
            h = Harness.BeginCombat<Ironclad>(
                new List<Harness.DeckEntry> { new(typeof(StrikeIronclad)) },
                shuffleSeed: 7,
                encounterId: "ENCOUNTER.TEST_SUBJECT_BOSS");

            // Same combat-start sequence EncounterSim runs: room-entry effects
            // (AdaptablePower + Enrage are applied here) and initial intents.
            foreach (var enemy in h.Enemies)
            {
                if (enemy.Monster != null)
                    await enemy.Monster.AfterAddedToRoom();
            }
            TurnHooks.RollEnemyMoves(h);

            var boss = h.Enemies.Single();
            var f = TestHelpers.ExpectPower(boss, "ADAPTABLE");
            if (f != null) return Fail(name, f);

            // ── Phase 1 down: not a win, corpse stays, respawn pending.
            await CreatureCmd.Kill(boss);
            if (boss.IsAlive) return Fail(name, "boss alive after phase-1 kill");
            if (EncounterSim.AllEnemiesDead(h))
                return Fail(name, "phase-1 kill counted as combat won (ShouldStopCombatFromEnding ignored)");
            if (boss.Monster!.NextMove.Id != "RESPAWN_MOVE")
                return Fail(name, $"expected RESPAWN_MOVE pending, got {boss.Monster.NextMove.Id} (AfterDeath hook didn't fire?)");

            // ── Enemy turn: the corpse acts and revives as phase 2.
            await TurnHooks.EnemyTurn(h, 1);
            if (!boss.IsAlive) return Fail(name, "boss did not revive into phase 2 on its turn");
            var p2 = TestHelpers.Expect(boss.MaxHp, 200, "phase 2 max HP");
            if (p2 != null) return Fail(name, p2);

            // ── Phase 2 down: still not a win; revives as phase 3.
            await CreatureCmd.Kill(boss);
            if (EncounterSim.AllEnemiesDead(h)) return Fail(name, "phase-2 kill counted as combat won");
            await TurnHooks.EnemyTurn(h, 2);
            if (!boss.IsAlive) return Fail(name, "boss did not revive into phase 3");
            var p3 = TestHelpers.Expect(boss.MaxHp, 300, "phase 3 max HP");
            if (p3 != null) return Fail(name, p3);
            // Phase 3 sheds AdaptablePower — the next death is real.
            if (boss.Powers.Any(p => p.Id.ToString().Contains("ADAPTABLE")))
                return Fail(name, "AdaptablePower still present in phase 3");

            // ── Phase 3 down: NOW it's a win.
            await CreatureCmd.Kill(boss);
            if (!EncounterSim.AllEnemiesDead(h))
                return Fail(name, "phase-3 kill did not end the combat");

            return new TestHelpers.TestResult(name, TestHelpers.Outcome.Pass);
        }
        catch (System.Exception ex)
        {
            return new TestHelpers.TestResult(name, TestHelpers.Outcome.Crash,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != null) Harness.EndCombat();
        }
    }

    private static TestHelpers.TestResult Fail(string name, string detail)
        => new(name, TestHelpers.Outcome.Fail, detail);
}
