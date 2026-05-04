using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Powers;

namespace StS2Sim.SilentTests;

/// <summary>
/// Shared scaffolding for the per-card test batteries. Each agent's bucket file
/// uses these patterns so we get consistent setup and consistent failure
/// reporting across hundreds of tests.
///
/// All tests run against an isolated combat with a Silent player and a single
/// BigDummy enemy. Energy is set to 3 by default; agents can override.
/// </summary>
internal static class TestHelpers
{
    /// <summary>
    /// One test result. Outcome buckets:
    /// - Pass: assertion held, expected value matched.
    /// - Fail: assertion didn't hold (test ran cleanly, just got the wrong answer).
    /// - Crash: harness threw an exception. ALWAYS a real bug — either missing
    ///         shim, missing relic effect, missing hook, etc. Triage these first.
    /// - Skipped: agent decided this card uses a mechanic we know is unimplemented
    ///           (multi-target, random target from pool, choose-card-from-discard).
    ///           Includes a reason so we can prioritize what to add next.
    /// </summary>
    public enum Outcome { Pass, Fail, Crash, Skipped }

    public sealed record TestResult(string Name, Outcome Outcome, string? Detail = null);

    /// <summary>Snapshot of state before a card is played. Tests assert against deltas.</summary>
    public record SnapshotBefore(int dummyHp, int playerHp, int playerBlock, int energy, int handSize, int drawSize, int discardSize, int exhaustSize);

    public static SnapshotBefore Snapshot(Harness.CombatHarness h)
    {
        var pcs = h.Player.PlayerCombatState!;
        return new SnapshotBefore(
            dummyHp: h.Dummy.CurrentHp,
            playerHp: h.Player.Creature.CurrentHp,
            playerBlock: h.Player.Creature.Block,
            energy: pcs.Energy,
            handSize: pcs.Hand.Cards.Count,
            drawSize: pcs.DrawPile.Cards.Count,
            discardSize: pcs.DiscardPile.Cards.Count,
            exhaustSize: pcs.ExhaustPile.Cards.Count);
    }

    // ─── Pattern 1: SingleCardTest ──────────────────────────────────────────
    //
    // The 80% case. Spin up combat with the card under test (plus optional
    // extras for draw-pile fodder), put it in hand, play it against the dummy,
    // run the assertion. Use this whenever the test reads as
    // "play X, expect Y damage / block / power".

    /// <summary>
    /// Test a single Silent card. The card gets put in hand and played at the dummy
    /// (or self for TargetType.Self cards). Assertion receives (harness, before-snapshot)
    /// and returns null on success or a failure reason string.
    /// </summary>
    public static async Task<TestResult> SingleCardTest<TCard>(
        string name,
        Func<Harness.CombatHarness, SnapshotBefore, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null,
        int upgradeLevel = 0,
        int energy = 3)
        where TCard : CardModel
    {
        var deck = new List<Harness.DeckEntry> { new(typeof(TCard), upgradeLevel) };
        if (extraDeckCards != null)
            deck.AddRange(extraDeckCards.Select(t => new Harness.DeckEntry(t)));

        return await RunInCombat(name, deck, async (h, _) =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);

            // Move the card under test into hand.
            var card = pcs.DrawPile.Cards.OfType<TCard>().First();
            pcs.DrawPile.RemoveInternal(card);
            pcs.Hand.AddInternal(card);

            var before = Snapshot(h);
            await PlayCard(h, card);
            return assert(h, before);
        });
    }

    // ─── Pattern 2: PowerThenPlayTest ───────────────────────────────────────
    //
    // For testing cards whose value comes from a power being active. Apply
    // the power directly (PowerCmd.Apply), then play the card under test.
    // Agents using this don't need to know the power's source card.

    /// <summary>
    /// Apply a Power to the player at a specific amount, then play a card.
    /// Use for "Strength makes Strike do +N", "Vulnerable on enemy makes Strike do 1.5x", etc.
    /// </summary>
    public static async Task<TestResult> PowerThenPlayTest<TPower, TCard>(
        string name,
        decimal powerAmount,
        bool powerOnEnemy,
        Func<Harness.CombatHarness, SnapshotBefore, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null,
        int upgradeLevel = 0,
        int energy = 3)
        where TPower : PowerModel
        where TCard : CardModel
    {
        var deck = new List<Harness.DeckEntry> { new(typeof(TCard), upgradeLevel) };
        if (extraDeckCards != null)
            deck.AddRange(extraDeckCards.Select(t => new Harness.DeckEntry(t)));

        return await RunInCombat(name, deck, async (h, _) =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);

            var target = powerOnEnemy ? h.Dummy : h.Player.Creature;
            await MegaCrit.Sts2.Core.Commands.PowerCmd.Apply<TPower>(
                target, powerAmount, h.Player.Creature, null);

            var card = pcs.DrawPile.Cards.OfType<TCard>().First();
            pcs.DrawPile.RemoveInternal(card);
            pcs.Hand.AddInternal(card);

            var before = Snapshot(h);
            await PlayCard(h, card);
            return assert(h, before);
        });
    }

    // ─── Pattern 3: SequenceTest ────────────────────────────────────────────
    //
    // Play multiple cards in order, assert against the final state. Use for
    // chains like "Bash then Strike", "Inflame then Strike", "Backstab then Backstab".

    /// <summary>
    /// Play a sequence of cards in order against the dummy (or self where appropriate).
    /// Assertion runs against the final harness state and the initial snapshot.
    /// </summary>
    public static async Task<TestResult> SequenceTest(
        string name,
        IReadOnlyList<Harness.DeckEntry> sequence,
        Func<Harness.CombatHarness, SnapshotBefore, string?> assert,
        IReadOnlyList<Type>? extraDeckCards = null,
        int energy = 99)
    {
        var deck = new List<Harness.DeckEntry>(sequence);
        if (extraDeckCards != null)
            deck.AddRange(extraDeckCards.Select(t => new Harness.DeckEntry(t)));

        return await RunInCombat(name, deck, async (h, _) =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);

            // Move every sequence card from draw pile to hand, in order.
            var cardsInHand = new List<CardModel>();
            foreach (var entry in sequence)
            {
                var c = pcs.DrawPile.Cards.FirstOrDefault(x => x.GetType() == entry.CardType
                    && (entry.UpgradeLevel == 0 || x.CurrentUpgradeLevel == entry.UpgradeLevel));
                if (c == null) return $"setup failed: could not find {entry.CardType.Name} (upgrade {entry.UpgradeLevel}) in draw pile";
                pcs.DrawPile.RemoveInternal(c);
                pcs.Hand.AddInternal(c);
                cardsInHand.Add(c);
            }

            var before = Snapshot(h);
            foreach (var c in cardsInHand)
                await PlayCard(h, c);
            return assert(h, before);
        });
    }

    // ─── Pattern 4: PreloadHandTest ─────────────────────────────────────────
    //
    // For cards whose effect depends on what's in your hand (HandTrick, Reflex,
    // CalculatedGamble) or in piles (Tactician requires being discarded).
    // Caller fully controls hand layout, then plays one card under test.

    /// <summary>
    /// Put a specific set of cards in hand (in addition to the card under test),
    /// then play the card under test against the dummy. Agents define expected
    /// behavior based on the constructed hand.
    /// </summary>
    public static async Task<TestResult> PreloadHandTest<TCard>(
        string name,
        IReadOnlyList<Type> handLoadout,
        Func<Harness.CombatHarness, SnapshotBefore, string?> assert,
        int upgradeLevel = 0,
        int energy = 3)
        where TCard : CardModel
    {
        var deck = new List<Harness.DeckEntry> { new(typeof(TCard), upgradeLevel) };
        deck.AddRange(handLoadout.Select(t => new Harness.DeckEntry(t)));

        return await RunInCombat(name, deck, async (h, _) =>
        {
            var pcs = h.Player.PlayerCombatState!;
            Reflect.SetEnergy(pcs, energy);

            // Move every card from draw pile to hand. Card under test goes last so it's
            // the most recently added, but order doesn't matter for play.
            foreach (var c in pcs.DrawPile.Cards.ToList())
            {
                pcs.DrawPile.RemoveInternal(c);
                pcs.Hand.AddInternal(c);
            }

            var card = pcs.Hand.Cards.OfType<TCard>().First();

            var before = Snapshot(h);
            await PlayCard(h, card);
            return assert(h, before);
        });
    }

    // ─── Skip helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Mark a test as skipped because the mechanic isn't implementable in the
    /// current harness. Reason should explain WHY (e.g. "needs random enemy
    /// target", "depends on monster intent", "requires CardSelectCmd from discard").
    /// </summary>
    public static Task<TestResult> Skip(string name, string reason)
        => Task.FromResult(new TestResult(name, Outcome.Skipped, reason));

    // ─── Internals ───────────────────────────────────────────────────────────

    private static async Task<TestResult> RunInCombat(
        string name,
        List<Harness.DeckEntry> deck,
        Func<Harness.CombatHarness, SnapshotBefore?, Task<string?>> body)
    {
        Harness.CombatHarness? h = null;
        try
        {
            h = Harness.BeginCombat<Silent>(deck, shuffleSeed: 1);
            var failure = await body(h, null);
            return failure is null
                ? new TestResult(name, Outcome.Pass)
                : new TestResult(name, Outcome.Fail, failure);
        }
        catch (Exception ex)
        {
            return new TestResult(name, Outcome.Crash,
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (h != null) Harness.EndCombat();
        }
    }

    /// <summary>
    /// Play a card with the right target (Self for TargetType.Self, dummy otherwise)
    /// and the right ResourceInfo. Most agents shouldn't need to call this directly —
    /// the SingleCardTest / SequenceTest helpers do it for you.
    /// </summary>
    public static async Task PlayCard(Harness.CombatHarness h, CardModel card)
    {
        var pcs = h.Player.PlayerCombatState!;

        // X-cost cards (Skewer, Outbreak, Malaise, Eradicate, Cascade, etc.)
        // need EnergyCost.CapturedXValue set to the energy actually spent before
        // OnPlayWrapper runs. The game's CardCmd.Play does this; we bypass that
        // path, so we replicate the capture here.
        if (card.EnergyCost.CostsX)
            card.EnergyCost.CapturedXValue = pcs.Energy;

        var cost = card.EnergyCost.GetResolved();
        var resources = new ResourceInfo
        {
            EnergySpent = cost,
            EnergyValue = cost,
            StarsSpent = 0,
            StarValue = 0,
        };
        var target = card.TargetType == MegaCrit.Sts2.Core.Entities.Cards.TargetType.Self
            ? h.Player.Creature
            : h.Dummy;

        pcs.LoseEnergy(cost);
        await card.OnPlayWrapper(h.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
    }

    /// <summary>Compose a "expected vs got" failure string. Returns null on match.</summary>
    public static string? Expect<T>(T actual, T expected, string what) where T : IEquatable<T>
        => actual.Equals(expected) ? null : $"expected {what}={expected}, got {actual}";

    /// <summary>
    /// Verify a power exists on a creature (case-insensitive, underscore-insensitive
    /// substring match). Game power IDs are <c>NO_DRAW_POWER</c>, <c>WRAITH_FORM_POWER</c>,
    /// <c>INFINITE_BLADES_POWER</c> etc.; tests can search for "NODRAW", "WRAITHFORM",
    /// "INFINITEBLADES", "WRAITH_FORM", or any variant — they all match.
    /// </summary>
    public static string? ExpectPower(MegaCrit.Sts2.Core.Entities.Creatures.Creature creature, string powerIdContains, int? expectedAmount = null)
    {
        static string norm(string s) => s.Replace("_", "");
        var needle = norm(powerIdContains);
        var found = creature.Powers.FirstOrDefault(p =>
            norm(p.Id.Entry).Contains(needle, StringComparison.OrdinalIgnoreCase));
        if (found == null)
            return $"expected power containing '{powerIdContains}', got [{string.Join(",", creature.Powers.Select(p => p.Id.Entry))}]";
        if (expectedAmount.HasValue && found.Amount != expectedAmount.Value)
            return $"expected {powerIdContains} amount={expectedAmount.Value}, got {found.Amount}";
        return null;
    }
}
