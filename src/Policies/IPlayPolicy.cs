using System;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// A policy decides which card from the player's hand to play next given the
/// current state — or returns null to end the turn.
/// </summary>
internal interface IPlayPolicy
{
    string Name { get; }
    /// <summary>Pick a card to play, or null to end turn.</summary>
    CardModel? ChooseCard(Harness.CombatHarness h, int energyLeft, Random rng);
}
