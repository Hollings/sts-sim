using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using StS2Sim.SilentTests;

namespace StS2Sim.CharTests;

/// <summary>
/// Character-generic version of <see cref="SilentTests.TestHelpers"/> for the
/// Regent / Necrobinder / Defect batteries. Differences from the Silent
/// scaffolding:
///  - combat spins up with any character, so orb slots, starter relics, and
///    the star counter all initialize through the same Player.CreateForNewRun
///    path the sims use;
///  - cards are played via card.SpendResources() — the game's own pre-play
///    debit — so star costs (Regent) and X-cost capture resolve exactly like
///    live play, instead of the energy-only manual debit the Silent helpers use;
///  - the snapshot carries stars / orb counts / Osty HP so assertions can
///    check character mechanics as simple deltas.
/// Reuses TestResult/Outcome from the Silent battery so both runners report
/// identically.
/// </summary>
internal static class CharTestHelpers
{
    /// <summary>State snapshot for delta assertions, extended with the three
    /// character resources (stars, orbs, Osty).</summary>
    public sealed record Snap(
        int dummyHp, int playerHp, int playerBlock, int energy, int stars,
        int handSize, int drawSize, int discardSize, int exhaustSize,
        int orbCount, int orbCapacity, int ostyHp, int ostyMaxHp);

    public static Snap Snapshot(Harness.CombatHarness h)
    {
        var pcs = h.Player.PlayerCombatState!;
        var osty = h.Player.Osty;
        return new Snap(
            dummyHp: h.Dummy.CurrentHp,
            playerHp: h.Player.Creature.CurrentHp,
            playerBlock: h.Player.Creature.Block,
            energy: pcs.Energy,
            stars: pcs.Stars,
            handSize: pcs.Hand.Cards.Count,
            drawSize: pcs.DrawPile.Cards.Count,
            discardSize: pcs.DiscardPile.Cards.Count,
            exhaustSize: pcs.ExhaustPile.Cards.Count,
            orbCount: pcs.OrbQueue.Orbs.Count,
            orbCapacity: pcs.OrbQueue.Capacity,
            ostyHp: osty != null && osty.IsAlive ? osty.CurrentHp : 0,
            ostyMaxHp: osty != null && osty.IsAlive ? osty.MaxHp : 0);
    }

    // ─── Pattern 1: free-form body ───────────────────────────────────────────

    /// <summary>
    /// Spin up combat for any character with the given deck and run an
    /// arbitrary async body. The body returns null on success or a failure
    /// reason. Use for tests that drive turn hooks (orb passives, relic
    /// combat-start effects) or build state by hand.
    /// </summary>
    public static async Task<TestHelpers.TestResult> Test<TChar>(
        string name,
        IReadOnlyList<Harness.DeckEntry> deck,
        Func<Harness.CombatHarness, Task<string?>> body)
        where TChar : CharacterModel
    {
        Harness.CombatHarness? h = null;
        try
        {
            h = Harness.BeginCombat<TChar>(deck, shuffleSeed: 1);
            var failure = await body(h);
            return failure is null
                ? new TestHelpers.TestResult(name, TestHelpers.Outcome.Pass)
                : new TestHelpers.TestResult(name, TestHelpers.Outcome.Fail, failure);
        }
        catch (Exception ex)
        {
            return new TestHelpers.TestResult(name, TestHelpers.Outcome.Crash,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != null) Harness.EndCombat();
        }
    }

    // ─── Pattern 2: play one card ────────────────────────────────────────────

    /// <summary>
    /// Put the card under test in hand and play it at the dummy (or self for
    /// TargetType.Self). <paramref name="setup"/> runs before the snapshot —
    /// use it to grant stars, summon Osty, channel orbs, etc.
    /// </summary>
    public static Task<TestHelpers.TestResult> Play1<TChar, TCard>(
        string name,
        Func<Harness.CombatHarness, Snap, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null,
        int upgradeLevel = 0,
        int energy = 9,
        Func<Harness.CombatHarness, Task>? setup = null)
        where TChar : CharacterModel
        where TCard : CardModel
    {
        var deck = new List<Harness.DeckEntry> { new(typeof(TCard), upgradeLevel) };
        if (extraDeckCards != null)
            deck.AddRange(extraDeckCards.Select(t => new Harness.DeckEntry(t)));

        return Test<TChar>(name, deck, async h =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);
            if (setup != null) await setup(h);

            var card = MoveToHand(h, typeof(TCard), upgradeLevel)
                ?? throw new InvalidOperationException($"{typeof(TCard).Name} not in draw pile");

            var before = Snapshot(h);
            await PlayCard(h, card);
            return assert(h, before);
        });
    }

    // ─── Pattern 3: play a sequence ──────────────────────────────────────────

    /// <summary>
    /// Play multiple cards in order against the dummy/self, assert against the
    /// final state vs the pre-sequence snapshot.
    /// </summary>
    public static Task<TestHelpers.TestResult> PlaySeq<TChar>(
        string name,
        IReadOnlyList<Harness.DeckEntry> sequence,
        Func<Harness.CombatHarness, Snap, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null,
        int energy = 99,
        Func<Harness.CombatHarness, Task>? setup = null)
        where TChar : CharacterModel
    {
        var deck = new List<Harness.DeckEntry>(sequence);
        if (extraDeckCards != null)
            deck.AddRange(extraDeckCards.Select(t => new Harness.DeckEntry(t)));

        return Test<TChar>(name, deck, async h =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);
            if (setup != null) await setup(h);

            var cardsInHand = new List<CardModel>();
            foreach (var entry in sequence)
            {
                var c = MoveToHand(h, entry.CardType, entry.UpgradeLevel);
                if (c == null) return $"setup failed: {entry.CardType.Name} not in draw pile";
                cardsInHand.Add(c);
            }

            var before = Snapshot(h);
            foreach (var c in cardsInHand)
                await PlayCard(h, c);
            return assert(h, before);
        });
    }

    // ─── Internals ───────────────────────────────────────────────────────────

    /// <summary>Move the first matching card from draw pile to hand.</summary>
    public static CardModel? MoveToHand(Harness.CombatHarness h, Type cardType, int upgradeLevel = 0)
    {
        var pcs = h.Player.PlayerCombatState!;
        var card = pcs.DrawPile.Cards.FirstOrDefault(x => x.GetType() == cardType
            && (upgradeLevel == 0 || x.CurrentUpgradeLevel == upgradeLevel));
        if (card == null) return null;
        pcs.DrawPile.RemoveInternal(card);
        pcs.Hand.AddInternal(card);
        return card;
    }

    /// <summary>
    /// Play a card through the game's own resource path: SpendResources()
    /// captures X values and debits energy AND stars (Regent), then
    /// OnPlayWrapper runs the card. Mirrors DamagePerTurnSim.RunPlayPhase.
    /// </summary>
    public static async Task PlayCard(Harness.CombatHarness h, CardModel card)
    {
        var (energySpent, starsSpent) = await card.SpendResources();
        var resources = new ResourceInfo
        {
            EnergySpent = energySpent,
            EnergyValue = energySpent,
            StarsSpent = starsSpent,
            StarValue = starsSpent,
        };
        var target = card.TargetType == TargetType.Self
            ? h.Player.Creature
            : h.Dummy;
        await card.OnPlayWrapper(h.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
    }
}
