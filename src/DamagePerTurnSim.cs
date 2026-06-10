using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models.Characters;

namespace StS2Sim;

/// <summary>
/// One trial = run N turns of "fill hand → play cards via policy → end turn".
/// Each trial spins up a fresh combat through <see cref="Harness"/> and tears
/// it down at the end. The dummy is healed back to full each turn so combat
/// never ends naturally — we measure damage dealt, not survivability.
/// </summary>
internal sealed class DamagePerTurnSim
{
    public required string DeckName { get; init; }
    public required IReadOnlyList<Harness.DeckEntry> Deck { get; init; }
    public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    public Type CharacterType { get; init; } = typeof(Ironclad);
    public int HandSize { get; init; } = 5;
    public int Turns { get; init; } = 5;
    public IPlayPolicy Policy { get; init; } = new GreedyAttackPolicy();
    public uint? PolicyRngSeed { get; init; } = null;

    public sealed record TurnResult(int Turn, int Damage, IReadOnlyList<PlayCapture.Event> Events);

    public sealed record TrialResult(uint Seed, IReadOnlyList<TurnResult> Turns)
    {
        public int TotalDamage => Turns.Sum(t => t.Damage);
        public double AvgPerTurn => Turns.Count == 0 ? 0 : (double)TotalDamage / Turns.Count;
    }

    public async Task<IReadOnlyList<TrialResult>> RunTrials(int trials)
    {
        var results = new List<TrialResult>();
        for (int t = 0; t < trials; t++)
        {
            var seed = (uint)(0xC0FFEE + t);
            results.Add(await RunSingleTrial(seed));
        }
        return results;
    }

    public async Task<TrialResult> RunSingleTrial(uint shuffleSeed)
    {
        var harness = Harness.BeginCombat(CharacterType, deckOverride: Deck, shuffleSeed: shuffleSeed, relicIds: Relics);
        var policyRng = new Random((int)(PolicyRngSeed ?? shuffleSeed ^ 0xDEAD_BEEFu));
        try
        {
            // Fire the at-combat-start hook so relics like Vajra (+1 Strength on enter)
            // and Bronze Scales apply their effects before turn 1.
            await TurnHooks.FireAfterRoomEntered(harness);

            var turnResults = new List<TurnResult>();
            for (int turn = 0; turn < Turns; turn++)
            {
                turnResults.Add(await RunSingleTurn(harness, turn + 1, policyRng));
            }
            return new TrialResult(shuffleSeed, turnResults);
        }
        finally
        {
            Harness.EndCombat();
        }
    }

    private async Task<TurnResult> RunSingleTurn(Harness.CombatHarness harness, int roundNumber, Random policyRng)
    {
        var pcs = harness.Player.PlayerCombatState!;

        // Side-turn-start half: bump RoundNumber → BeforeTurnStart snapshots →
        // ClearBlock-via-AfterTurnStart → energy reset → BeforeSideTurnStart →
        // AfterSideTurnStart → reset X-cost capture. After this returns, the
        // hand still has last turn's leftovers; nothing's been drawn yet.
        await TurnHooks.PrepareSideTurnStart(harness, roundNumber);

        var hpBefore = harness.Dummy.CurrentHp;

        // Chronological per-turn event log: every draw and every play (manual
        // or auto) recorded in the order it actually happened. Started after
        // side-turn-start so non-draw setup hooks don't pollute the timeline,
        // but before the actual hand draw fires AfterCardDrawn.
        var events = new List<PlayCapture.Event>();
        PlayCapture.Start(events);

        // Player-turn-start half: AfterEnergyReset → BeforeHandDraw →
        // ModifyHandDraw → Draw (fires AfterCardDrawn → recorded) →
        // AfterPlayerTurnStart(Early/regular/Late). Mirrors the player-turn-
        // start block in CombatManager.
        await TurnHooks.PlayerTurnStartDraw(harness, HandSize);

        await PlayPhase(harness, pcs, policyRng);

        PlayCapture.Stop();
        var hpAfter = harness.Dummy.CurrentHp;

        // Full game-flow end-of-turn: BeforeTurnEnd hooks → exhaust Ethereal →
        // trigger TurnEndInHand cards → BeforeFlush hooks → discard via
        // CardPileCmd.Add (fires AfterCardDiscarded so Tingsha/ToughBandages work) →
        // EndOfTurnCleanup → AfterTurnEnd. Mirrors EndPlayerTurnPhaseOneInternal.
        await TurnHooks.EndOfPlayerTurn(harness);

        // Heal dummy back to full so HP doesn't ever hit 0.
        Reflect.HealToFull(harness.Dummy);

        return new TurnResult(roundNumber, hpBefore - hpAfter, events);
    }

    private async Task PlayPhase(Harness.CombatHarness harness, MegaCrit.Sts2.Core.Entities.Players.PlayerCombatState pcs, Random policyRng)
    {
        // No energy gate here: 0-cost cards (Shivs!) are playable at 0 energy,
        // exactly like the real game. The loop ends when the policy has nothing
        // playable left. The cap is a safety valve against a policy/engine
        // interaction that never drains the hand.
        const int maxPlaysPerTurn = 500;
        for (int plays = 0; plays < maxPlaysPerTurn; plays++)
        {
            var card = Policy.ChooseCard(harness, pcs.Energy, policyRng);
            if (card == null) break;

            if (card.EnergyCost.GetAmountToSpend() > pcs.Energy) break; // policy lied; bail

            var target = card.TargetType == TargetType.Self
                ? harness.Player.Creature
                : harness.Dummy;
            // SpendResources is the game's own pre-play debit (PlayCardAction
            // does the same): captures X on X-cost cards (Whirlwind spends all
            // energy instead of playing at X=0), debits energy/stars, and fires
            // AfterEnergySpent. Cards that gain energy mid-OnPlay (Offering,
            // Bloodletting) are visible to the policy on the next iteration.
            var (energySpent, starsSpent) = await card.SpendResources();
            var resources = new ResourceInfo
            {
                EnergySpent = energySpent,
                EnergyValue = energySpent,
                StarsSpent = starsSpent,
                StarValue = starsSpent,
            };
            PlayCapture.RecordManualPlay(card);
            await card.OnPlayWrapper(harness.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
        }
    }
}
