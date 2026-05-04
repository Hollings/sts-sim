using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Unlocks;

namespace StS2Sim;

/// <summary>
/// One-time bootstrap of game runtime data + helpers for spinning up an isolated
/// combat with a single player and a single dummy enemy.
/// </summary>
internal static class Harness
{
    private static bool _bootstrapped;

    public static void Bootstrap()
    {
        if (_bootstrapped) return;
        _bootstrapped = true;

        TestMode.TurnOnInternal();
        if (!NonInteractiveMode.IsActive)
            throw new InvalidOperationException("NonInteractiveMode failed to activate.");

        ModelDb.Init();
        ModelIdSerializationCache.Init();
        ModelDb.InitIds();

        GodotShims.ApplyLocalizationShim();

        // Cards like Armaments ("pick a card to upgrade") wait for a CardSelectCmd
        // selector. Without one, they NRE. Register a global auto-picker.
        MegaCrit.Sts2.Core.Commands.CardSelectCmd.UseSelector(new AutoCardSelector());

        // PrefsSave is never loaded in headless mode; cards (e.g. Silent's Neutralize)
        // read SaveManager.Instance.PrefsSave.FastMode for animation timing and NRE
        // without this initialization.
        var sm = SaveManager.Instance;
        var prefsMgrField = typeof(SaveManager)
            .GetField("_prefsSaveManager", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("SaveManager._prefsSaveManager not found");
        var prefsMgr = prefsMgrField.GetValue(sm)!;
        var prefsProperty = prefsMgr.GetType().GetProperty("Prefs")
            ?? throw new InvalidOperationException("PrefsSaveManager.Prefs not found");
        if (prefsProperty.GetValue(prefsMgr) == null)
            prefsProperty.SetValue(prefsMgr, new PrefsSave());
    }

    public sealed class CombatHarness
    {
        public required Player Player { get; init; }
        public required Creature Dummy { get; init; }
        public required CombatState State { get; init; }
        public required PlayerChoiceContext Ctx { get; init; }
    }

    /// <summary>One card in a deck override: the C# type plus optional upgrade level.</summary>
    public sealed record DeckEntry(Type CardType, int UpgradeLevel = 0);

    /// <summary>Sugar so callers with a plain List&lt;Type&gt; don't have to map themselves.</summary>
    public static IReadOnlyList<DeckEntry> AsEntries(IEnumerable<Type> types)
        => types.Select(t => new DeckEntry(t)).ToList();

    public static CombatHarness BeginCombat<TCharacter>(IEnumerable<Type>? deckOverride = null, ulong netId = 1UL, uint shuffleSeed = 1u, IEnumerable<string>? relicIds = null)
        where TCharacter : CharacterModel
        => BeginCombat<TCharacter>(
            deckOverride?.Select(t => new DeckEntry(t)),
            netId, shuffleSeed, relicIds);

    /// <summary>
    /// Maps a save-file character ID (e.g. "CHARACTER.SILENT") to its C# Type.
    /// Returns null if the ID doesn't match any known character.
    /// </summary>
    public static Type? ResolveCharacterType(string characterId)
        => ModelDb.AllCharacters.FirstOrDefault(c => c.Id.ToString() == characterId)?.GetType();

    /// <summary>Runtime-type overload for callers that receive character type dynamically.</summary>
    public static CombatHarness BeginCombat(Type characterType, IEnumerable<DeckEntry>? deckOverride, ulong netId = 1UL, uint shuffleSeed = 1u, IEnumerable<string>? relicIds = null)
        => (CombatHarness)BeginCombatGeneric
            .MakeGenericMethod(characterType)
            .Invoke(null, new object?[] { deckOverride, netId, shuffleSeed, relicIds })!;

    // Resolve the DeckEntry-taking generic overload of BeginCombat<T>. There are
    // two generic overloads (one takes IEnumerable<Type>, one IEnumerable<DeckEntry>);
    // we pick the latter by checking the first parameter's IEnumerable<T> arg.
    private static readonly MethodInfo BeginCombatGeneric = typeof(Harness)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .First(m =>
        {
            if (!m.IsGenericMethod || m.Name != nameof(BeginCombat)) return false;
            var pt = m.GetParameters()[0].ParameterType;
            return pt.IsGenericType
                && pt.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                && pt.GetGenericArguments()[0] == typeof(DeckEntry);
        });

    public static CombatHarness BeginCombat<TCharacter>(IEnumerable<DeckEntry>? deckOverride, ulong netId = 1UL, uint shuffleSeed = 1u, IEnumerable<string>? relicIds = null)
        where TCharacter : CharacterModel
    {
        var character = ModelDb.Character<TCharacter>();
        var unlockState = new UnlockState(Array.Empty<string>(), Array.Empty<ModelId>(), 0);
        var player = Player.CreateForNewRun(character, unlockState, netId);

        if (deckOverride != null)
        {
            ReplaceDeck(player, deckOverride);
        }

        if (relicIds != null)
        {
            ReplaceRelics(player, relicIds);
        }

        var combat = new CombatState(encounter: null, runState: NullRunState.Instance);
        player.ResetCombatState();
        combat.AddPlayer(player);
        player.PopulateCombatState(new Rng(shuffleSeed), combat);

        Reflect.AttachCombatState(combat);
        Reflect.SetCombatInProgress(true);

        var dummyMonster = (MonsterModel)ModelDb.Monster<BigDummy>().ToMutable();
        var dummy = combat.CreateCreature(dummyMonster, CombatSide.Enemy, "slot1");
        combat.AddCreature(dummy);

        return new CombatHarness
        {
            Player = player,
            Dummy = dummy,
            State = combat,
            Ctx = new BlockingPlayerChoiceContext(),
        };
    }

    public static void EndCombat()
    {
        Reflect.SetCombatInProgress(false);
        Reflect.AttachCombatState(null);

        // CombatHistory is on the singleton CombatManager and accumulates across
        // every trial we run; without clearing it grows unbounded over a long
        // sim (millions of entries).
        CombatManager.Instance.History.Clear();
    }

    private static readonly MethodInfo ModelDbCardGeneric =
        typeof(ModelDb).GetMethod(nameof(ModelDb.Card))
        ?? throw new InvalidOperationException("ModelDb.Card<T> not found");

    private static void ReplaceDeck(Player player, IEnumerable<DeckEntry> entries)
    {
        var deck = player.Deck;
        // Wipe canonical starting deck.
        foreach (var card in deck.Cards.ToList())
        {
            deck.RemoveInternal(card);
        }
        foreach (var entry in entries)
        {
            var canonical = (CardModel)ModelDbCardGeneric.MakeGenericMethod(entry.CardType).Invoke(null, null)!;
            var copy = (CardModel)canonical.ToMutable();
            copy.FloorAddedToDeck = 1;
            // Apply upgrades. Mirrors CardModel.FromSerializable: each level
            // is one UpgradeInternal + one FinalizeUpgradeInternal.
            for (int i = 0; i < entry.UpgradeLevel; i++)
            {
                copy.UpgradeInternal();
                copy.FinalizeUpgradeInternal();
            }
            // Owner must be set BEFORE the card lands in any pile, otherwise the
            // CardModel.Pile property (which derives from owner.Piles.Find) returns
            // null and CardPileCmd.Add bails with "no owner".
            copy.Owner = player;
            deck.AddInternal(copy);
        }
    }

    /// <summary>
    /// Replace the player's relics (which start as the character's StartingRelics)
    /// with the actual relic list from the save file. Without this, only the
    /// starter relic fires hooks — Bag of Marbles, Vajra, Lantern, etc. all silently no-op.
    /// </summary>
    private static void ReplaceRelics(Player player, IEnumerable<string> relicIds)
    {
        var relicsField = typeof(Player)
            .GetField("_relics", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Player._relics not found");
        var existing = (System.Collections.IList)relicsField.GetValue(player)!;
        existing.Clear();

        foreach (var id in relicIds)
        {
            if (string.IsNullOrEmpty(id)) continue;
            ModelId modelId;
            try { modelId = ModelId.Deserialize(id); }
            catch { continue; }
            var relic = ModelDb.GetByIdOrNull<RelicModel>(modelId);
            if (relic == null) continue;
            var copy = (RelicModel)relic.ToMutable();
            copy.FloorAddedToDeck = 1;
            player.AddRelicInternal(copy, silent: true);
        }
    }

}
