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
///   - <c>CardCmd.Preview</c> Harmony prefix      → sets SubjectLabel on last event
///   - <see cref="AutoCardSelector"/>             → sets SubjectLabel on last event
///
/// Thread-static: each parallel sim worker (if/when we add them) gets its
/// own capture sink without locking.
/// </summary>
internal static class PlayCapture
{
    public enum EventKind { Draw, Play, EnemyMove }

    public sealed class Event
    {
        public EventKind Kind { get; }
        public string Label { get; }
        public bool Auto { get; }
        public string? SubjectLabel { get; set; }

        public Event(EventKind kind, string label, bool auto)
        {
            Kind = kind;
            Label = label;
            Auto = auto;
        }
    }

    [System.ThreadStatic] private static List<Event>? _sink;
    [System.ThreadStatic] private static Event? _lastEvent;

    public static void Start(List<Event> sink) => _sink = sink;
    public static void Stop() => _sink = null;

    public static void RecordDraw(CardModel card)
    {
        if (_sink == null) return;
        var ev = new Event(EventKind.Draw, CardLabels.Format(card), auto: false);
        _lastEvent = ev;
        _sink.Add(ev);
    }

    public static void RecordAutoPlay(CardModel card)
    {
        if (_sink == null) return;
        var ev = new Event(EventKind.Play, CardLabels.Format(card), auto: true);
        _lastEvent = ev;
        _sink.Add(ev);
    }

    public static void RecordManualPlay(CardModel card)
    {
        if (_sink == null) return;
        var ev = new Event(EventKind.Play, CardLabels.Format(card), auto: false);
        _lastEvent = ev;
        _sink.Add(ev);
    }

    /// <summary>
    /// Called when a card play affects another card (Hidden Gem buffs a card,
    /// Nightmare selects a card, etc.). Sets SubjectLabel on the last event.
    /// </summary>
    public static void RecordEffectSubject(CardModel card)
    {
        if (_sink == null || _lastEvent == null) return;
        _lastEvent.SubjectLabel = CardLabels.Format(card);
    }

    /// <summary>Encounter mode: an enemy performed a move ("Vantom · Ink Blot — 7 dmg").</summary>
    public static void RecordEnemyMove(string label, string? subject = null)
    {
        if (_sink == null) return;
        var ev = new Event(EventKind.EnemyMove, label, auto: false) { SubjectLabel = subject };
        _lastEvent = ev;
        _sink.Add(ev);
    }
}
