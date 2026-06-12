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
        /// <summary>First enemy. In dummy mode this is the 9999-HP BigDummy.</summary>
        public required Creature Dummy { get; init; }
        /// <summary>All enemy creatures (1 in dummy mode, 1+ in encounter mode).</summary>
        public required IReadOnlyList<Creature> Enemies { get; init; }
        /// <summary>Set in encounter mode; null when fighting the dummy.</summary>
        public EncounterModel? Encounter { get; init; }
        public required CombatState State { get; init; }
        public required PlayerChoiceContext Ctx { get; init; }
    }

    /// <summary>One card in a deck override: the C# type plus optional upgrade
    /// level and enchantment (e.g. ENCHANTMENT.INSTINCT doubles powered attack
    /// damage — dropping it would sim a materially weaker deck).</summary>
    public sealed record DeckEntry(Type CardType, int UpgradeLevel = 0, string? EnchantmentId = null, int EnchantmentAmount = 0);

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
    public static CombatHarness BeginCombat(Type characterType, IEnumerable<DeckEntry>? deckOverride, ulong netId = 1UL, uint shuffleSeed = 1u, IEnumerable<string>? relicIds = null, string? encounterId = null, IReadOnlyList<string>? monsterIds = null)
    {
        try
        {
            return (CombatHarness)BeginCombatGeneric
                .MakeGenericMethod(characterType)
                .Invoke(null, new object?[] { deckOverride, netId, shuffleSeed, relicIds, encounterId, monsterIds })!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // Re-throw the real failure with its original stack — the reflection
            // wrapper makes every harness bug look identical.
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw; // unreachable
        }
    }

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

    public static CombatHarness BeginCombat<TCharacter>(IEnumerable<DeckEntry>? deckOverride, ulong netId = 1UL, uint shuffleSeed = 1u, IEnumerable<string>? relicIds = null, string? encounterId = null, IReadOnlyList<string>? monsterIds = null)
        where TCharacter : CharacterModel
    {
        var character = ModelDb.Character<TCharacter>();
        var unlockState = new UnlockState(Array.Empty<string>(), Array.Empty<ModelId>(), 0);
        var player = Player.CreateForNewRun(character, unlockState, netId);

        if (deckOverride != null)
        {
            ReplaceDeck(player, deckOverride);
        }
        else
        {
            // Starter-deck mode: PopulateStartingInventory leaves card.Owner
            // null (the real game assigns owners when the player joins a run,
            // which we never do). CombatState.Contains dereferences Owner on
            // every hook iteration, so claim the cards here — exactly what
            // ReplaceDeck does for override decks.
            foreach (var card in player.Deck.Cards)
                card.Owner = player;
        }

        if (relicIds != null)
        {
            ReplaceRelics(player, relicIds);
        }

        // Encounter mode: generate the real monster lineup for the chosen
        // encounter. The mutable encounter instance is per-trial state (its
        // monsters are mutable clones), so this is cheap to redo each trial.
        EncounterModel? encounter = null;
        if (encounterId != null)
        {
            var canonical = ModelDb.GetByIdOrNull<EncounterModel>(ModelId.Deserialize(encounterId))
                ?? throw new ArgumentException($"Unknown encounter id '{encounterId}'");
            encounter = canonical.ToMutable();
            // Some encounters randomize their composition; seed that roll from
            // the trial seed instead of NullRunState's constant stream.
            Reflect.SeedEncounterRng(encounter, shuffleSeed ^ 0x5EEDBEEFu);
            encounter.GenerateMonstersWithSlots(NullRunState.Instance);
        }

        var combat = new CombatState(encounter, runState: NullRunState.Instance);
        player.ResetCombatState();
        combat.AddPlayer(player);
        player.PopulateCombatState(new Rng(shuffleSeed), combat);

        Reflect.AttachCombatState(combat);
        Reflect.SetCombatInProgress(true);

        var enemies = new List<Creature>();
        if (encounter != null)
        {
            uint monsterSeed = shuffleSeed;
            foreach (var (monster, slot) in encounter.MonstersWithSlots)
            {
                var creature = combat.CreateCreature(monster, CombatSide.Enemy, slot);
                combat.AddCreature(creature);
                // CombatManager.AddCreature does this in the real game: builds
                // the move state machine. Without it NextMove stays UNSET and
                // TakeTurn throws.
                monster.SetUpForCombat();
                Reflect.ReseedMonsterRng(monster, monsterSeed++);
                enemies.Add(creature);
            }
        }
        else if (monsterIds is { Count: > 0 })
        {
            // Mirror mode (combat advisor): build an arbitrary enemy lineup
            // from MONSTER.* ids — the same plumbing as encounter mode minus
            // the EncounterModel. Caller force-sets HP/powers/intents after.
            uint monsterSeed = shuffleSeed ^ 0x0DDBA11u;
            int slotNum = 1;
            foreach (var id in monsterIds)
            {
                var canonical = ModelDb.GetByIdOrNull<MonsterModel>(ModelId.Deserialize(id))
                    ?? throw new ArgumentException($"Unknown monster id '{id}'");
                var monster = (MonsterModel)canonical.ToMutable();
                var creature = combat.CreateCreature(monster, CombatSide.Enemy, $"slot{slotNum++}");
                combat.AddCreature(creature);
                monster.SetUpForCombat();
                Reflect.ReseedMonsterRng(monster, monsterSeed++);
                enemies.Add(creature);
            }
        }
        else
        {
            var dummyMonster = (MonsterModel)ModelDb.Monster<BigDummy>().ToMutable();
            var dummy = combat.CreateCreature(dummyMonster, CombatSide.Enemy, "slot1");
            combat.AddCreature(dummy);
            // The dummy never takes a turn, but cards that manipulate enemy
            // moves (Whistle's stun rewires the move state machine) NRE if it
            // was never built.
            dummyMonster.SetUpForCombat();
            enemies.Add(dummy);
        }

        return new CombatHarness
        {
            Player = player,
            Dummy = enemies[0],
            Enemies = enemies,
            Encounter = encounter,
            State = combat,
            Ctx = new BlockingPlayerChoiceContext(),
        };
    }

    public static void EndCombat()
    {
        Reflect.SetCombatInProgress(false);
        Reflect.AttachCombatState(null);
        // A trial where the player died set CombatManager._pendingLoss, which
        // would gate all damage in every later trial. See Reflect.ClearPendingLoss.
        Reflect.ClearPendingLoss();

        // CombatHistory is on the singleton CombatManager and accumulates across
        // every trial we run; without clearing it grows unbounded over a long
        // sim (millions of entries).
        CombatManager.Instance.History.Clear();
    }

    private static readonly MethodInfo ModelDbCardGeneric =
        typeof(ModelDb).GetMethod(nameof(ModelDb.Card))
        ?? throw new InvalidOperationException("ModelDb.Card<T> not found");

    // ReplaceDeck runs once per trial in the sim hot loop (~thousands/sec);
    // MakeGenericMethod + Invoke per card is measurable there. The canonical
    // model is immutable and process-wide, so cache it per card type.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, CardModel> CanonicalCardCache = new();

    private static CardModel CanonicalCard(Type cardType)
        => CanonicalCardCache.GetOrAdd(cardType,
            t => (CardModel)ModelDbCardGeneric.MakeGenericMethod(t).Invoke(null, null)!);

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
            var canonical = CanonicalCard(entry.CardType);
            var copy = (CardModel)canonical.ToMutable();
            copy.FloorAddedToDeck = 1;
            // Apply upgrades. Mirrors CardModel.FromSerializable: each level
            // is one UpgradeInternal + one FinalizeUpgradeInternal.
            for (int i = 0; i < entry.UpgradeLevel; i++)
            {
                copy.UpgradeInternal();
                copy.FinalizeUpgradeInternal();
            }
            // Apply the enchantment exactly like CardModel.FromSerializable:
            // EnchantInternal attaches it, ModifyCard lets it rewrite the
            // card's stats (Instinct's damage doubling, cost changes, etc.).
            if (!string.IsNullOrEmpty(entry.EnchantmentId))
            {
                var canonicalEnchant = ModelDb.GetByIdOrNull<EnchantmentModel>(ModelId.Deserialize(entry.EnchantmentId));
                if (canonicalEnchant != null)
                {
                    var enchant = (EnchantmentModel)canonicalEnchant.ToMutable();
                    enchant.Amount = entry.EnchantmentAmount;
                    copy.EnchantInternal(enchant, entry.EnchantmentAmount);
                    copy.Enchantment!.ModifyCard();
                }
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
