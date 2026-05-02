using System.Collections.Generic;
using MegaCrit.Sts2.Core.Models;

namespace StS2Sim;

/// <summary>
/// Per-turn chronological log of card events: draws and plays interleaved in
/// the order they actually occurred. Lets the UI show cascades like
/// "drew Pommel Strike → played Pommel Strike (auto) → drew Strike (from Pommel)".
///
/// Sources that append:
///   - <c>Hook.AfterCardDrawn</c> Harmony prefix → "draw" event
///   - <c>CardCmd.AutoPlay</c> Harmony prefix    → "play" event (auto)
///   - <see cref="DamagePerTurnSim"/> policy loop → "play" event (manual)
///
/// Thread-static: each parallel sim worker (if/when we add them) gets its
/// own capture sink without locking.
/// </summary>
internal static class PlayCapture
{
    public enum EventKind { Draw, Play }

    public sealed record Event(EventKind Kind, string Label, bool Auto);

    [System.ThreadStatic] private static List<Event>? _sink;

    public static void Start(List<Event> sink) => _sink = sink;
    public static void Stop() => _sink = null;

    public static void RecordDraw(CardModel card)
    {
        if (_sink == null) return;
        _sink.Add(new Event(EventKind.Draw, CardLabels.Format(card), Auto: false));
    }

    public static void RecordAutoPlay(CardModel card)
    {
        if (_sink == null) return;
        _sink.Add(new Event(EventKind.Play, CardLabels.Format(card), Auto: true));
    }

    public static void RecordManualPlay(CardModel card)
    {
        if (_sink == null) return;
        _sink.Add(new Event(EventKind.Play, CardLabels.Format(card), Auto: false));
    }
}
