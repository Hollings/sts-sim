using System;
using System.Reflection;
using HarmonyLib;

namespace StS2Sim;

/// <summary>
/// We run outside the Godot engine, so any code path that hits the Godot
/// native interop (Godot.OS, ResourceLoader, etc.) crashes with 0xC0000005.
/// We Harmony-patch the offending entry points to be no-ops so the game's
/// pure-logic types can be used without a SceneTree.
///
/// Each patch should be the smallest possible "lie": say the equivalent of
/// "we're not in the editor / there's no command line / no resource available"
/// so behavior elsewhere stays sane.
/// </summary>
internal static class GodotShims
{
    public static void Apply()
    {
        var harmony = new Harmony("StS2Sim.GodotShims");

        // Direct typeof() reference is safe: it returns Type metadata without
        // triggering the type's static constructor.
        PatchPrefix(
            harmony,
            type: typeof(MegaCrit.Sts2.Core.Logging.Logger),
            methodName: "GetIsRunningFromGodotEditor",
            prefix: typeof(GodotShims).GetMethod(nameof(IsRunningFromGodotEditor_Prefix), BindingFlags.Static | BindingFlags.NonPublic)!);

        // Game logs go through Godot.GD.Print which P/Invokes — redirect to .NET console.
        PatchPrefix(
            harmony,
            type: typeof(MegaCrit.Sts2.Core.Logging.ConsoleLogPrinter),
            methodName: "Print",
            prefix: typeof(GodotShims).GetMethod(nameof(ConsoleLogPrinter_Print_Prefix), BindingFlags.Static | BindingFlags.NonPublic)!);

        // Phase 2 attempt: leave IterateHookListeners alone — we want hooks to fire so
        // Strength/Vulnerable/etc. modify damage. This requires every model
        // (relic/card/power) in the chain to have its Owner properly set.

        // Godot.Time.GetTicksMsec() goes through native interop. Game uses it for
        // animation duration math which collapses to zero in NonInteractiveMode anyway.
        PatchPrefix(
            harmony,
            type: typeof(Godot.Time),
            methodName: "GetTicksMsec",
            prefix: typeof(GodotShims).GetMethod(nameof(Time_GetTicksMsec_Prefix), BindingFlags.Static | BindingFlags.NonPublic)!);
    }

    private static void PatchPrefix(Harmony harmony, Type? type, string methodName, MethodInfo prefix)
    {
        if (type == null) throw new InvalidOperationException("Type not found for patch: " + methodName);
        var target = AccessTools.Method(type, methodName);
        if (target == null) throw new InvalidOperationException($"Method {type.FullName}.{methodName} not found for patch");
        harmony.Patch(target, prefix: new HarmonyMethod(prefix));
    }

    // Prefix: skip original, return false (we're never the Godot editor).
    private static bool IsRunningFromGodotEditor_Prefix(ref bool __result)
    {
        __result = false;
        return false; // false = skip original
    }

    private static bool ConsoleLogPrinter_Print_Prefix(MegaCrit.Sts2.Core.Logging.LogLevel logLevel, string text)
    {
        Console.WriteLine($"[{logLevel.ToString().ToUpperInvariant()}] {text}");
        return false;
    }

    private static readonly System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.AbstractModel> _emptyListeners
        = System.Array.Empty<MegaCrit.Sts2.Core.Models.AbstractModel>();

    private static bool IterateHookListeners_Prefix(ref System.Collections.Generic.IEnumerable<MegaCrit.Sts2.Core.Models.AbstractModel> __result)
    {
        __result = _emptyListeners;
        return false;
    }

    private static bool Time_GetTicksMsec_Prefix(ref ulong __result)
    {
        __result = 0;
        return false;
    }

    // Localization isn't initialized in headless mode, so Creature.ToString() (which
    // hits LocString.GetFormattedText()) throws. Patch it to return a safe identifier.
    public static void ApplyLocalizationShim()
    {
        var harmony = new Harmony("StS2Sim.LocShim");
        harmony.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Entities.Creatures.Creature), nameof(object.ToString)),
            prefix: new HarmonyMethod(typeof(GodotShims).GetMethod(nameof(Creature_ToString_Prefix), BindingFlags.Static | BindingFlags.NonPublic)!));
    }

    private static bool Creature_ToString_Prefix(MegaCrit.Sts2.Core.Entities.Creatures.Creature __instance, ref string __result)
    {
        __result = __instance.IsMonster
            ? $"<Monster {__instance.Monster!.Id}>"
            : $"<Player {__instance.Player!.Character.Id}>";
        return false;
    }
}
