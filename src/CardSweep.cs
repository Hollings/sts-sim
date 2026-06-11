using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.CardPools;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Characters;
using MegaCrit.Sts2.Core.Models.Orbs;

namespace StS2Sim;

/// <summary>
/// Coverage battery for CARDS: play every playable card in every pool once
/// (base and upgraded) in an isolated combat and report anything that crashes
/// or hangs. No value assertions — this is the "does the harness survive this
/// card at all" map, the per-card analogue of <see cref="EncounterSweep"/>.
///
/// Each trial preps state so conditional paths actually run instead of
/// silently whiffing: 9 energy, 10 stars (Regent costs), a 5-HP Osty
/// (Necrobinder), two channeled orbs (Defect evokes), three cards in the
/// discard pile (Dredge-style pickers), one extra card in hand (discard
/// costs). Hangs (an unshimmed UI/netcode wait) are caught by a timeout —
/// note a hung play keeps running in the background, so results after the
/// first HANG can cascade; fix hangs first.
///
/// Invoke: <c>dotnet run -c Release -- card-sweep</c>. Exit 2 on any
/// crash/hang.
/// </summary>
internal static class CardSweep
{
    private static readonly TimeSpan PlayTimeout = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAll()
    {
        Console.WriteLine("=== StS2 Card Sweep — play every card once, base + upgraded ===\n");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Character pools run with their own character; shared pools run as
        // Ironclad (tokens like Shiv/Soul/SovereignBlade still execute their
        // OnPlay — owner character only matters for whiff-vs-crash, and
        // whiffs are fine here).
        var jobs = new List<(string Pool, Type CharType, CardModel Card)>();
        foreach (var character in ModelDb.AllCharacters)
            foreach (var card in character.CardPool.AllCards)
                jobs.Add((character.CardPool.Title, character.GetType(), card));
        foreach (var pool in new CardPoolModel[]
        {
            ModelDb.CardPool<ColorlessCardPool>(),
            ModelDb.CardPool<EventCardPool>(),
            ModelDb.CardPool<QuestCardPool>(),
            ModelDb.CardPool<TokenCardPool>(),
            ModelDb.CardPool<StatusCardPool>(),
            ModelDb.CardPool<CurseCardPool>(),
        })
            foreach (var card in pool.AllCards)
                jobs.Add((pool.Title, typeof(Ironclad), card));

        // Colorless cards show up in every character pool query; the pool
        // lists themselves don't overlap, but dedupe by id defensively.
        var seen = new HashSet<string>();
        jobs = jobs.Where(j => seen.Add(j.Card.Id.ToString())).ToList();

        int played = 0, crashes = 0, hangs = 0, skipped = 0;
        var failures = new List<string>();
        string currentPool = "";

        foreach (var (pool, charType, card) in jobs)
        {
            if (pool != currentPool)
            {
                currentPool = pool;
                Console.WriteLine($"\n--- {pool} ({jobs.Count(j => j.Pool == pool)} cards) ---");
            }

            if (card.Keywords.Contains(CardKeyword.Unplayable))
            {
                skipped++;
                continue;
            }

            foreach (var upgrade in card.IsUpgradable ? new[] { 0, 1 } : new[] { 0 })
            {
                var label = $"{CardLabels.PrettyName(card.Id.ToString())}{(upgrade > 0 ? "+" : "")}";
                var detail = await PlayOnce(charType, card.GetType(), upgrade);
                played++;
                if (detail == null) continue;

                var kind = detail.StartsWith("HANG") ? "HANG " : "CRASH";
                if (kind == "HANG ") hangs++; else crashes++;
                failures.Add($"[{pool}] {label} — {detail}");
                Console.WriteLine($"  [{kind}] {label,-28} {detail}");
            }
        }

        sw.Stop();
        Console.WriteLine($"\n=== Summary ===");
        Console.WriteLine($"  {played} plays · {crashes} crash · {hangs} hang · {skipped} unplayable skipped");
        Console.WriteLine($"  Elapsed: {sw.Elapsed.TotalSeconds:F1}s");
        if (failures.Count > 0)
        {
            Console.WriteLine("\n=== Failures ===");
            foreach (var f in failures) Console.WriteLine("  " + f);
        }
        return crashes + hangs > 0 ? 2 : 0;
    }

    /// <summary>One isolated combat: prep state, play the card, tear down.
    /// Returns null on success, otherwise a crash/hang description.</summary>
    private static async Task<string?> PlayOnce(Type charType, Type cardType, int upgrade)
    {
        try
        {
            var deck = new List<Harness.DeckEntry> { new(cardType, upgrade) };
            for (int i = 0; i < 6; i++)
                deck.Add(new Harness.DeckEntry(typeof(StrikeIronclad)));

            var h = Harness.BeginCombat(charType, deck, shuffleSeed: 7);
            try
            {
                var pcs = h.Player.PlayerCombatState!;
                Reflect.SetEnergy(pcs, 9);
                pcs.GainStars(10);
                if (charType == typeof(Necrobinder))
                    await OstyCmd.Summon(h.Ctx, h.Player, 5m, null);
                if (charType == typeof(Defect))
                {
                    await OrbCmd.Channel<LightningOrb>(h.Ctx, h.Player);
                    await OrbCmd.Channel<FrostOrb>(h.Ctx, h.Player);
                }

                // Hand: card under test + one fodder (discard-a-card costs).
                // Discard pile: three fodder (Dredge-style pickers).
                var card = pcs.DrawPile.Cards.First(c => c.GetType() == cardType);

                // Mad Science's type is a saved property the Tinker Time
                // event sets; a fresh copy has none and throws by design.
                // Force a valid configuration so the play path is exercised.
                if (card is MadScience ms)
                    ms.TinkerTimeType = CardType.Attack;
                pcs.DrawPile.RemoveInternal(card);
                pcs.Hand.AddInternal(card);
                foreach (var (fodder, idx) in pcs.DrawPile.Cards.ToList().Select((c, i) => (c, i)).Take(4))
                {
                    pcs.DrawPile.RemoveInternal(fodder);
                    if (idx == 0) pcs.Hand.AddInternal(fodder);
                    else pcs.DiscardPile.AddInternal(fodder);
                }

                var playTask = PlayCard(h, card);
                var winner = await Task.WhenAny(playTask, Task.Delay(PlayTimeout));
                if (winner != playTask)
                    return $"HANG (>{PlayTimeout.TotalSeconds:F0}s — unshimmed UI/netcode wait?)";
                await playTask; // propagate any exception
                return null;
            }
            finally
            {
                Harness.EndCombat();
            }
        }
        catch (Exception ex)
        {
            return CrashDetail(ex);
        }
    }

    private static async Task PlayCard(Harness.CombatHarness h, CardModel card)
    {
        var (energySpent, starsSpent) = await card.SpendResources();
        var resources = new ResourceInfo
        {
            EnergySpent = energySpent,
            EnergyValue = energySpent,
            StarsSpent = starsSpent,
            StarValue = starsSpent,
        };
        var target = card.TargetType switch
        {
            TargetType.Self or TargetType.AnyPlayer or TargetType.None or TargetType.TargetedNoCreature
                => h.Player.Creature,
            TargetType.Osty or TargetType.AnyAlly or TargetType.AllAllies
                => h.Player.Osty ?? h.Player.Creature,
            _ => h.Dummy,
        };
        await card.OnPlayWrapper(h.Ctx, target, isAutoPlay: true, resources, skipCardPileVisuals: true);
    }

    private static string CrashDetail(Exception ex)
    {
        var frames = (ex.StackTrace ?? "").Split('\n')
            .Where(f => f.Contains("MegaCrit") || f.Contains("StS2Sim"))
            .Take(3).Select(f => f.Trim());
        var msg = ex.Message;
        var nl = msg.IndexOf('\n');
        if (nl >= 0) msg = msg[..nl].Trim();
        return $"{ex.GetType().Name}: {msg}\n      {string.Join("\n      ", frames)}";
    }
}
