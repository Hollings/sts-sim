using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;

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
        PatchPrefix(harmony, typeof(Logger), "GetIsRunningFromGodotEditor", nameof(IsRunningFromGodotEditor_Prefix));

        // Game logs go through Godot.GD.Print which P/Invokes — redirect to .NET console.
        PatchPrefix(harmony, typeof(ConsoleLogPrinter), "Print", nameof(ConsoleLogPrinter_Print_Prefix));

        // Godot.Time.GetTicksMsec() goes through native interop. Game uses it for
        // animation duration math which collapses to zero in NonInteractiveMode anyway.
        PatchPrefix(harmony, typeof(Godot.Time), "GetTicksMsec", nameof(Time_GetTicksMsec_Prefix));

        // CardCmd.AutoPlay fires for any auto-played card (Hellraiser strikes,
        // Havoc top-of-deck plays, etc.). The policy loop only sees plays it
        // chose itself, so without this autoplays would go unrecorded in the
        // turn log even though they deal real damage.
        PatchPrefix(harmony, typeof(CardCmd), "AutoPlay", nameof(CardCmd_AutoPlay_Prefix));

        // Hook.AfterCardDrawn fires once per drawn card from CardPileCmd.Draw.
        // Patching it as a prefix means we record the draw BEFORE Hellraiser-
        // style autoplay fires, giving us a chronological event timeline:
        // "drew Pommel Strike" → "played Pommel Strike (auto)" → "drew Strike".
        PatchPrefix(harmony, typeof(MegaCrit.Sts2.Core.Hooks.Hook), "AfterCardDrawn", nameof(Hook_AfterCardDrawn_Prefix));

        // CardPileCmd.Shuffle calls Engine.GetMainLoop() for animation pacing
        // (sleeps between adding cards back to draw pile). We replace the whole
        // method with a synchronous shuffle that does the same logical work
        // without ever touching SceneTree.
        PatchPrefix(harmony, typeof(CardPileCmd), "Shuffle", nameof(CardPileCmd_Shuffle_Prefix));
    }

    /// <summary>
    /// Localization isn't initialized in headless mode, so Creature.ToString() (which
    /// hits LocString.GetFormattedText()) throws. Patch it to return a safe identifier.
    /// Applied after ModelDb.Init so it can target the resolved type.
    /// </summary>
    public static void ApplyLocalizationShim()
    {
        var harmony = new Harmony("StS2Sim.LocShim");
        harmony.Patch(
            AccessTools.Method(typeof(Creature), nameof(object.ToString)),
            prefix: new HarmonyMethod(GetPrefix(nameof(Creature_ToString_Prefix))));
    }

    private static void PatchPrefix(Harmony harmony, Type? type, string methodName, string prefixName)
    {
        if (type == null) throw new InvalidOperationException("Type not found for patch: " + methodName);
        var target = AccessTools.Method(type, methodName);
        if (target == null) throw new InvalidOperationException($"Method {type.FullName}.{methodName} not found for patch");
        harmony.Patch(target, prefix: new HarmonyMethod(GetPrefix(prefixName)));
    }

    private static MethodInfo GetPrefix(string name)
        => typeof(GodotShims).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"Prefix method {name} not found");

    // ─── Prefixes ──────────────────────────────────────────────────────────

    private static bool IsRunningFromGodotEditor_Prefix(ref bool __result)
    {
        __result = false;
        return false; // false = skip original
    }

    private static bool ConsoleLogPrinter_Print_Prefix(LogLevel logLevel, string text)
    {
        Console.WriteLine($"[{logLevel.ToString().ToUpperInvariant()}] {text}");
        return false;
    }

    private static bool Time_GetTicksMsec_Prefix(ref ulong __result)
    {
        __result = 0;
        return false;
    }

    private static void CardCmd_AutoPlay_Prefix(CardModel card)
        => PlayCapture.RecordAutoPlay(card);

    private static void Hook_AfterCardDrawn_Prefix(CardModel card)
        => PlayCapture.RecordDraw(card);

    private static bool Creature_ToString_Prefix(Creature __instance, ref string __result)
    {
        __result = __instance.IsMonster
            ? $"<Monster {__instance.Monster!.Id}>"
            : $"<Player {__instance.Player!.Character.Id}>";
        return false;
    }

    // Replacement for CardPileCmd.Shuffle that skips the per-card animation wait
    // (which calls Engine.GetMainLoop() and crashes outside Godot). Logic mirrors
    // the original: pull discards, shuffle by player's RNG, re-add to draw pile.
    private static bool CardPileCmd_Shuffle_Prefix(PlayerChoiceContext choiceContext, Player player, ref Task __result)
    {
        __result = ShuffleSync(player);
        return false;
    }

    private static Task ShuffleSync(Player player)
    {
        var pcs = player.PlayerCombatState;
        if (pcs == null) return Task.CompletedTask;

        var draw = pcs.DrawPile;
        var discard = pcs.DiscardPile;
        var combined = new List<CardModel>(discard.Cards);

        var inDraw = new HashSet<CardModel>(draw.Cards);
        foreach (var c in inDraw)
        {
            draw.RemoveInternal(c, silent: true);
            combined.Add(c);
        }

        // Fisher-Yates with the player's shuffle RNG. Sufficient for our purposes —
        // the game's StableShuffle has tie-breaking semantics for IComparable<T>
        // that CardModel doesn't satisfy, but card identity isn't tied here.
        var rng = player.RunState.Rng.Shuffle;
        for (int i = combined.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(0, i + 1);
            (combined[i], combined[j]) = (combined[j], combined[i]);
        }

        // Re-add to draw pile (we drop the per-card wait because it's animation-only).
        foreach (var c in combined)
        {
            if (inDraw.Contains(c)) draw.AddInternal(c, -1, silent: true);
            else
            {
                discard.RemoveInternal(c, silent: true);
                draw.AddInternal(c, -1, silent: true);
            }
        }
        return Task.CompletedTask;
    }
}
