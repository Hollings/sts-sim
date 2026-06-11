using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace StS2Sim;

/// <summary>
/// Centralized private-member pokes for the bits of game state we need to drive
/// the headless harness. Each helper documents *why* the official API doesn't
/// suffice — usually because the public path runs Godot-coupled side effects
/// (RNG-seeded shuffles, GodotEvent invocations, atlas loaders) that we can't
/// afford in a sim hot loop.
/// </summary>
internal static class Reflect
{
    private static readonly PropertyInfo CombatManagerIsInProgress =
        typeof(CombatManager).GetProperty("IsInProgress", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("CombatManager.IsInProgress not found");

    private static readonly FieldInfo CombatManagerStateField =
        typeof(CombatManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("CombatManager._state not found");

    private static readonly PropertyInfo PlayerCombatStateEnergy =
        typeof(PlayerCombatState).GetProperty("Energy", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("PlayerCombatState.Energy not found");

    private static readonly PropertyInfo CreatureCurrentHp =
        typeof(Creature).GetProperty("CurrentHp", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Creature.CurrentHp not found");

    public static void SetCombatInProgress(bool value)
        => CombatManagerIsInProgress.SetValue(CombatManager.Instance, value);

    private static readonly FieldInfo CombatManagerPendingLoss =
        typeof(CombatManager).GetField("_pendingLoss", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("CombatManager._pendingLoss not found");

    /// <summary>
    /// When the player dies, the game calls CombatManager.LoseCombat() which
    /// sets _pendingLoss — and IsEnding stays true until the combat-teardown
    /// path (Reset) clears it. We never run that path, and AttackCommand gates
    /// all damage on IsOverOrEnding, so ONE lethal trial would silently turn
    /// every subsequent trial in the process into a 0-damage no-op. Clear it
    /// on every EndCombat.
    /// </summary>
    public static void ClearPendingLoss()
        => CombatManagerPendingLoss.SetValue(CombatManager.Instance, null);

    public static void AttachCombatState(CombatState? state)
        => CombatManagerStateField.SetValue(CombatManager.Instance, state);

    public static void SetEnergy(PlayerCombatState pcs, int amount)
        => PlayerCombatStateEnergy.SetValue(pcs, amount);

    public static void HealToFull(Creature creature)
        => CreatureCurrentHp.SetValue(creature, creature.MaxHp);

    /// <summary>Test scaffolding: place a creature at an exact HP (e.g. inside
    /// a Doom threshold) without routing through the damage pipeline.</summary>
    public static void SetCurrentHp(Creature creature, int hp)
        => CreatureCurrentHp.SetValue(creature, hp);

    private static readonly PropertyInfo CreatureMaxHp =
        typeof(Creature).GetProperty("MaxHp", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Creature.MaxHp not found");

    private static readonly PropertyInfo CreatureBlock =
        typeof(Creature).GetProperty("Block", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Creature.Block not found");

    /// <summary>State mirroring: force max HP without the heal/clamp side
    /// effects of CreatureCmd.SetMaxHp. Set this BEFORE SetCurrentHp.</summary>
    public static void SetMaxHp(Creature creature, int maxHp)
        => CreatureMaxHp.SetValue(creature, maxHp);

    /// <summary>State mirroring: force current block (private setter).</summary>
    public static void SetBlock(Creature creature, int block)
        => CreatureBlock.SetValue(creature, block);

    private static readonly PropertyInfo PowerAmount =
        typeof(PowerModel).GetProperty("Amount", BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException("PowerModel.Amount not found");

    /// <summary>State mirroring: force a power's stack count to an exact
    /// value, bypassing the apply-time modifier hooks.</summary>
    public static void SetPowerAmount(PowerModel power, int amount)
        => PowerAmount.SetValue(power, amount);

    private static readonly FieldInfo MonsterRunRngField =
        typeof(MonsterModel).GetField("_runRng", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("MonsterModel._runRng not found");

    private static readonly FieldInfo EncounterRngField =
        typeof(EncounterModel).GetField("_rng", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("EncounterModel._rng not found");

    /// <summary>
    /// NullRunState.Rng mints a fresh RunRngSet("") on every access, so the
    /// stream a monster captures in CreateCreature is IDENTICAL across trials —
    /// every seed would see the same boss AI script. Re-seed per trial so boss
    /// behavior varies with the shuffle seed (and stays reproducible). The
    /// public setter throws once set, hence reflection.
    /// </summary>
    public static void ReseedMonsterRng(MonsterModel monster, uint seed)
    {
        MonsterRunRngField.SetValue(monster, new RunRngSet($"sim{seed}"));
        monster.Rng = new Rng(seed);
    }

    /// <summary>
    /// EncounterModel seeds its monster-generation RNG from RunState.Rng.Seed,
    /// which is constant under NullRunState — randomized encounter compositions
    /// would be identical every trial without this.
    /// </summary>
    public static void SeedEncounterRng(EncounterModel encounter, uint seed)
        => EncounterRngField.SetValue(encounter, new Rng(seed));
}
