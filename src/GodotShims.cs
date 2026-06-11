using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
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

        // CardCmd.Preview is called by cards like Hidden Gem to highlight the card
        // they buffed. Capture the previewed card as the "subject" of the last event
        // so the UI can display "Hidden Gem → Shiv+".
        harmony.Patch(
            AccessTools.Method(typeof(CardCmd), "Preview",
                new[] { typeof(CardModel), typeof(float), typeof(CardPreviewStyle) }),
            prefix: new HarmonyMethod(GetPrefix(nameof(CardCmd_Preview_Prefix))));

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

        // LocManager.Instance is null in headless mode (never initialized without Godot).
        // Cards like Nightmare call SelectionScreenPrompt which calls LocString.Exists(),
        // and some paths call GetFormattedText/GetRawText for display strings.
        // Return safe fallbacks so cards with selection prompts can still be played.
        harmony.Patch(
            AccessTools.Method(typeof(LocString), "Exists", new[] { typeof(string), typeof(string) }),
            prefix: new HarmonyMethod(GetPrefix(nameof(LocString_Exists_Prefix))));
        harmony.Patch(
            AccessTools.Method(typeof(LocString), "GetFormattedText"),
            prefix: new HarmonyMethod(GetPrefix(nameof(LocString_GetText_Prefix))));
        harmony.Patch(
            AccessTools.Method(typeof(LocString), "GetRawText"),
            prefix: new HarmonyMethod(GetPrefix(nameof(LocString_GetText_Prefix))));

        // CardPileCmd.Shuffle calls Engine.GetMainLoop() for animation pacing
        // (sleeps between adding cards back to draw pile). We replace the whole
        // method with a synchronous shuffle that does the same logical work
        // without ever touching SceneTree.
        PatchPrefix(harmony, typeof(CardPileCmd), "Shuffle", nameof(CardPileCmd_Shuffle_Prefix));

        // NGame.Instance (the root UI node) is dereferenced WITHOUT null checks
        // in several monster moves — Vantom's Dismember calls
        // NGame.Instance.DoHitStop, Amalgamator calls ScreenShakeTrauma, etc.
        // Plant a constructor-skipped instance (no Godot native init runs) and
        // no-op every screen-effect member that game code calls on it.
        var ngame = typeof(MegaCrit.Sts2.Core.Nodes.NGame);
        var instanceBacking = ngame.GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("NGame.Instance backing field not found");
        instanceBacking.SetValue(null, System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(ngame));
        PatchPrefix(harmony, ngame, "ScreenShake", nameof(NoOp_Prefix));
        PatchPrefix(harmony, ngame, "ScreenRumble", nameof(NoOp_Prefix));
        PatchPrefix(harmony, ngame, "ScreenShakeTrauma", nameof(NoOp_Prefix));
        PatchPrefix(harmony, ngame, "DoHitStop", nameof(NoOp_Prefix));
        harmony.Patch(
            AccessTools.PropertyGetter(ngame, "CurrentRunNode"),
            prefix: new HarmonyMethod(GetPrefix(nameof(NGame_CurrentRunNode_Prefix))));
        // DecimillipedeSegment measures the screen via GetViewportRect (native).
        // Declared on CanvasItem (Control only inherits it).
        harmony.Patch(
            AccessTools.Method(typeof(Godot.CanvasItem), "GetViewportRect"),
            prefix: new HarmonyMethod(GetPrefix(nameof(GetViewportRect_Prefix))));

        // NCombatRoom.Instance (=> NRun.Instance?.CombatRoom, null headless) is
        // also dereferenced without null checks in ~40 monster/card callsites
        // (SlumberingBeetle.AfterAddedToRoom, Flyconid, KinFollower, ...). Serve
        // a constructor-skipped stub from the getter and null/no-op the members
        // those callsites touch — their RESULTS are almost always null-checked.
        var ncombatRoom = typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom);
        harmony.Patch(
            AccessTools.PropertyGetter(ncombatRoom, "Instance"),
            prefix: new HarmonyMethod(GetPrefix(nameof(NCombatRoom_Instance_Prefix))));
        harmony.Patch(
            AccessTools.Method(ncombatRoom, "GetCreatureNode"),
            prefix: new HarmonyMethod(GetPrefix(nameof(NCombatRoom_GetCreatureNode_Prefix))));
        PatchPrefix(harmony, ncombatRoom, "RadialBlur", nameof(NoOp_Prefix));
        // Necrobinder's Unleash shakes the Osty pet's visual node on the room.
        PatchPrefix(harmony, ncombatRoom, "ShakeOstyIfDead", nameof(NoOp_Prefix));
        harmony.Patch(
            AccessTools.PropertyGetter(ncombatRoom, "Background"),
            prefix: new HarmonyMethod(GetPrefix(nameof(NCombatRoom_Background_Prefix))));
        harmony.Patch(
            AccessTools.PropertyGetter(ncombatRoom, "CombatVfxContainer"),
            prefix: new HarmonyMethod(GetPrefix(nameof(NCombatRoom_VfxContainer_Prefix))));

        // Mid-combat spawns (Fabricator's bots, Fogmog) call the UI-side
        // NCombatRoom.AddCreature to build the creature's visual node — pure
        // rendering, NREs on the stub. The logic-side CombatManager.AddCreature
        // is what actually matters and runs separately.
        PatchPrefix(harmony, ncombatRoom, "AddCreature", nameof(NoOp_Prefix));

        // OrbCmd.AddSlots (Capacitor, etc.) dereferences
        // GetCreatureNode(player).OrbManager WITHOUT a null guard — the only
        // orb-path UI call that doesn't (RemoveSlots uses ?.) — so it NREs on
        // our null creature nodes. Replicate its two lines of capacity math
        // and skip the slot animation.
        harmony.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.OrbCmd), "AddSlots"),
            prefix: new HarmonyMethod(GetPrefix(nameof(OrbCmd_AddSlots_Prefix))));
        // RemoveSlots (Bulk Up) has the same unguarded GetCreatureNode(...)
        // dereference — same treatment.
        harmony.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.OrbCmd), "RemoveSlots"),
            prefix: new HarmonyMethod(GetPrefix(nameof(OrbCmd_RemoveSlots_Prefix))));

        // The co-op "protect a teammate" powers (Flanking, Intercept's Covered,
        // Knockdown, Tag Team) override AfterApplied solely to render the
        // applying PLAYER's name into a hover-tip var via
        // RunManager.Instance.NetService — netcode we don't have. The gameplay
        // effect lives in their Modify* hooks, which still run; only the
        // display string is lost.
        foreach (var powerType in new[]
        {
            typeof(MegaCrit.Sts2.Core.Models.Powers.FlankingPower),
            typeof(MegaCrit.Sts2.Core.Models.Powers.CoveredPower),
            typeof(MegaCrit.Sts2.Core.Models.Powers.KnockdownPower),
            typeof(MegaCrit.Sts2.Core.Models.Powers.TagTeamPower),
        })
        {
            harmony.Patch(
                AccessTools.Method(powerType, "AfterApplied"),
                prefix: new HarmonyMethod(GetPrefix(nameof(CompletedTask_Prefix))));
        }

        // PlayerCmd.EndTurn (Void Form: "play this, your turn ends") drives
        // CombatManager's multiplayer ready-up machinery → RunManager NRE.
        // Our TurnHooks own the turn flow, so just raise a flag the play
        // loop checks — the card's real semantic (no more plays this turn)
        // is preserved.
        harmony.Patch(
            AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.PlayerCmd), "EndTurn"),
            prefix: new HarmonyMethod(GetPrefix(nameof(PlayerCmd_EndTurn_Prefix))));

        // Knowledge Demon (and anything else forcing a "choose a card" screen)
        // calls CardSelectCmd.FromChooseACardScreen, which drives a UI flow.
        // Auto-pick the first option, mirroring AutoCardSelector's heuristic.
        PatchPrefix(harmony, typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd),
            "FromChooseACardScreen", nameof(FromChooseACardScreen_Prefix));

        // CardSelectCmd.FromSimpleGrid (Dredge's "return cards from discard",
        // and similar pickers) only consults the installed ICardSelector when
        // LocalContext.IsMe(player) — false headless — and otherwise awaits a
        // REMOTE multiplayer choice that never arrives (hang), after touching
        // RunManager netcode singletons (crash). Route straight to our
        // AutoCardSelector and skip the choice-sync machinery entirely.
        PatchPrefix(harmony, typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd),
            "FromSimpleGrid", nameof(FromSimpleGrid_Prefix));

        // Kaiser Crab is rendered as a background scene: BOTH its monsters
        // (Rocket, Crusher) drive every move through a Background property
        // that resolves a UI node. Serve an uninitialized stub from those
        // properties and no-op the stub's animation methods.
        var crabBg = typeof(MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground);
        foreach (var monsterType in new[]
        {
            typeof(MegaCrit.Sts2.Core.Models.Monsters.Rocket),
            typeof(MegaCrit.Sts2.Core.Models.Monsters.Crusher),
        })
        {
            harmony.Patch(
                AccessTools.PropertyGetter(monsterType, "Background"),
                prefix: new HarmonyMethod(GetPrefix(nameof(KaiserCrabBackground_Prefix))));
        }
        // No-op every public method the stub could be asked to perform —
        // they're all animation drivers (PlayAttackAnim, PlayHurtAnim,
        // PlayRightSideChargeUpAnim, ...). Enumerating them by hand is
        // whack-a-mole; the class is pure visuals.
        foreach (var method in crabBg.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (method.IsSpecialName) continue; // property accessors
            if (method.ReturnType == typeof(Task))
                harmony.Patch(method, prefix: new HarmonyMethod(GetPrefix(nameof(CompletedTask_Prefix))));
            else if (method.ReturnType == typeof(void))
                harmony.Patch(method, prefix: new HarmonyMethod(GetPrefix(nameof(NoOp_Prefix))));
        }

        // new NodePath("...") marshals the string through native interop —
        // a hard 0xC0000005 process kill, NOT a catchable exception. It runs
        // during ARGUMENT evaluation, so even `nullThing.GetNode("path")`
        // (which would throw a tame NRE) dies natively first. Skip the native
        // init; the resulting NodePath is hollow, but every headless code path
        // that builds one is about to NRE on a null UI node anyway — and an
        // NRE we can catch and report beats a dead process.
        harmony.Patch(
            AccessTools.Constructor(typeof(Godot.NodePath), new[] { typeof(string) }),
            prefix: new HarmonyMethod(GetPrefix(nameof(NoOp_Prefix))));
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

    private static bool NoOp_Prefix() => false;

    private static bool CompletedTask_Prefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground? _crabBgStub;

    private static bool KaiserCrabBackground_Prefix(ref MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground __result)
    {
        _crabBgStub ??= (MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(MegaCrit.Sts2.Core.Nodes.Vfx.Backgrounds.NKaiserCrabBossBackground));
        __result = _crabBgStub;
        return false;
    }

    private static bool FromChooseACardScreen_Prefix(
        System.Collections.Generic.IReadOnlyList<CardModel> cards,
        ref Task<CardModel?> __result)
    {
        var pick = cards.Count > 0 ? cards[0] : null;
        if (pick != null) PlayCapture.RecordEffectSubject(pick);
        __result = Task.FromResult<CardModel?>(pick);
        return false;
    }

    private static bool FromSimpleGrid_Prefix(
        System.Collections.Generic.IReadOnlyList<CardModel> cardsIn,
        MegaCrit.Sts2.Core.CardSelection.CardSelectorPrefs prefs,
        ref Task<System.Collections.Generic.IEnumerable<CardModel>> __result)
    {
        var selector = MegaCrit.Sts2.Core.Commands.CardSelectCmd.Selector;
        if (selector == null) return true; // no headless selector installed; run the original
        __result = selector.GetSelectedCards(cardsIn, prefs.MinSelect, prefs.MaxSelect);
        return false;
    }

    private static bool NGame_CurrentRunNode_Prefix(ref MegaCrit.Sts2.Core.Nodes.NRun? __result)
    {
        __result = null;
        return false;
    }

    private static bool GetViewportRect_Prefix(ref Godot.Rect2 __result)
    {
        __result = new Godot.Rect2(0, 0, 1920, 1080);
        return false;
    }

    private static MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom? _combatRoomStub;

    private static bool NCombatRoom_Instance_Prefix(ref MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom? __result)
    {
        _combatRoomStub ??= (MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom)
            System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(
                typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NCombatRoom));
        __result = _combatRoomStub;
        return false;
    }

    private static bool NCombatRoom_GetCreatureNode_Prefix(ref MegaCrit.Sts2.Core.Nodes.Combat.NCreature? __result)
    {
        __result = null;
        return false;
    }

    // Mirrors OrbCmd.AddSlots minus the AddSlotAnim UI call (capacity is
    // capped at OrbQueue.maxCapacity = 10 in the original).
    private static bool OrbCmd_AddSlots_Prefix(
        MegaCrit.Sts2.Core.Entities.Players.Player player, int amount, ref Task __result)
    {
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsOverOrEnding)
        {
            var queue = player.PlayerCombatState.OrbQueue;
            queue.AddCapacity(Math.Min(10 - queue.Capacity, amount));
        }
        __result = Task.CompletedTask;
        return false;
    }

    // Mirrors OrbCmd.RemoveSlots minus the RemoveSlotAnim UI call.
    // RemoveCapacity itself evicts orbs over the new capacity.
    private static bool OrbCmd_RemoveSlots_Prefix(
        MegaCrit.Sts2.Core.Entities.Players.Player player, int amount)
    {
        if (!MegaCrit.Sts2.Core.Combat.CombatManager.Instance.IsOverOrEnding)
        {
            var queue = player.PlayerCombatState.OrbQueue;
            queue.RemoveCapacity(Math.Min(queue.Capacity, amount));
        }
        return false;
    }

    /// <summary>
    /// Set when a card play requests the turn to end (Void Form). The play
    /// loop (<see cref="DamagePerTurnSim.RunPlayPhase"/>) checks and clears
    /// this at the right moments; single-card test plays can ignore it.
    /// </summary>
    internal static bool EndTurnRequested;

    private static bool PlayerCmd_EndTurn_Prefix()
    {
        EndTurnRequested = true;
        return false;
    }

    private static bool NCombatRoom_Background_Prefix(ref MegaCrit.Sts2.Core.Nodes.Rooms.NCombatBackground? __result)
    {
        __result = null;
        return false;
    }

    private static bool NCombatRoom_VfxContainer_Prefix(ref Godot.Control? __result)
    {
        __result = null;
        return false;
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

    private static bool CardCmd_Preview_Prefix(CardModel card)
    {
        PlayCapture.RecordEffectSubject(card);
        return false; // skip visual preview entirely in headless mode
    }

    private static void CardCmd_AutoPlay_Prefix(CardModel card)
        => PlayCapture.RecordAutoPlay(card);

    private static void Hook_AfterCardDrawn_Prefix(CardModel card)
        => PlayCapture.RecordDraw(card);

    private static bool LocString_Exists_Prefix(ref bool __result)
    {
        if (LocManager.Instance != null) return true; // let original run
        __result = true; // pretend every key exists so callers don't throw
        return false;
    }

    private static bool LocString_GetText_Prefix(ref string __result)
    {
        if (LocManager.Instance != null) return true;
        __result = string.Empty;
        return false;
    }

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
        __result = ShuffleSync(choiceContext, player);
        return false;
    }

    private static Task ShuffleSync(PlayerChoiceContext choiceContext, Player player)
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

        // Fire AfterShuffle on each listener — StratagemPower, BiiigHug, TheAbacus
        // hook here. Without this, those cards/relics are silent during sims.
        return FireAfterShuffle(choiceContext, player);
    }

    private static async Task FireAfterShuffle(PlayerChoiceContext choiceContext, Player player)
    {
        var combat = player.Creature.CombatState;
        if (combat == null) return;
        foreach (var listener in combat.IterateHookListeners().ToList())
            await listener.AfterShuffle(choiceContext, player);
    }
}
