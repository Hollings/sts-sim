using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;
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
    }

    public sealed class CombatHarness
    {
        public required Player Player { get; init; }
        public required Creature Dummy { get; init; }
        public required CombatState State { get; init; }
        public required PlayerChoiceContext Ctx { get; init; }
    }

    public static CombatHarness BeginCombat<TCharacter>(IEnumerable<Type>? deckOverride = null, ulong netId = 1UL, uint shuffleSeed = 1u)
        where TCharacter : CharacterModel
    {
        var character = ModelDb.Character<TCharacter>();
        var unlockState = new UnlockState(Array.Empty<string>(), Array.Empty<ModelId>(), 0);
        var player = Player.CreateForNewRun(character, unlockState, netId);

        if (deckOverride != null)
        {
            ReplaceDeck(player, deckOverride);
        }

        var combat = new CombatState(encounter: null, runState: NullRunState.Instance);
        player.ResetCombatState();
        combat.AddPlayer(player);
        player.PopulateCombatState(new Rng(shuffleSeed), combat);

        AttachCombatStateToManager(combat);
        SetCombatInProgress(true);

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
        SetCombatInProgress(false);
        var field = typeof(CombatManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(CombatManager.Instance, null);
    }

    private static void ReplaceDeck(Player player, IEnumerable<Type> cardTypes)
    {
        var deck = player.Deck;
        // Wipe canonical starting deck.
        foreach (var card in deck.Cards.ToList())
        {
            deck.RemoveInternal(card);
        }
        foreach (var t in cardTypes)
        {
            var canonical = (CardModel)typeof(ModelDb)
                .GetMethod(nameof(ModelDb.Card))!
                .MakeGenericMethod(t)
                .Invoke(null, null)!;
            var copy = (CardModel)canonical.ToMutable();
            copy.FloorAddedToDeck = 1;
            // Owner must be set BEFORE the card lands in any pile, otherwise the
            // CardModel.Pile property (which derives from owner.Piles.Find) returns
            // null and CardPileCmd.Add bails with "no owner".
            copy.Owner = player;
            deck.AddInternal(copy);
        }
    }

    private static void SetCombatInProgress(bool value)
    {
        typeof(CombatManager).GetProperty("IsInProgress", BindingFlags.Public | BindingFlags.Instance)!
            .SetValue(CombatManager.Instance, value);
    }

    private static void AttachCombatStateToManager(CombatState state)
    {
        typeof(CombatManager).GetField("_state", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(CombatManager.Instance, state);
    }
}
