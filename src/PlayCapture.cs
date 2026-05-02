using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Per-turn record of every card play, including auto-plays (Hellraiser
/// drawn-strike, Havoc top-of-deck, etc.). The autoplay Harmony prefix in
/// <see cref="GodotShims"/> appends here whenever <c>CardCmd.AutoPlay</c>
/// fires; the policy loop appends manual plays. Both feed the same list so
/// the final order matches what actually happened in combat.
///
/// Thread-static: each parallel sim worker (if/when we add them) gets its
/// own capture sink without locking.
/// </summary>
internal static class PlayCapture
{
    [System.ThreadStatic] private static List<string>? _sink;

    public static void Start(List<string> sink) => _sink = sink;
    public static void Stop() => _sink = null;

    /// <summary>Called from the AutoPlay Harmony prefix. No-op if no capture is active.</summary>
    public static void RecordAutoPlay(CardModel card)
    {
        if (_sink == null) return;
        _sink.Add(CardLabels.Format(card) + " (auto)");
    }
}
