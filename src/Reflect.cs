using System;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;

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

    public static void AttachCombatState(CombatState? state)
        => CombatManagerStateField.SetValue(CombatManager.Instance, state);

    public static void SetEnergy(PlayerCombatState pcs, int amount)
        => PlayerCombatStateEnergy.SetValue(pcs, amount);

    public static void HealToFull(Creature creature)
        => CreatureCurrentHp.SetValue(creature, creature.MaxHp);
}
