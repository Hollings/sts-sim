using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StS2Sim;

/// <summary>
/// Reads the vanilla game's current_run.save (or current_run_mp.save if newer)
/// and pulls out the active deck. Pure file IO + JSON; no mod, no game running.
/// </summary>
internal static class SaveFileReader
{
    public sealed record Deck
    {
        public required string SourcePath { get; init; }
        public required DateTime Modified { get; init; }
        public required string CharacterId { get; init; }
        public required IReadOnlyList<DeckCard> Cards { get; init; }
        public int CurrentHp { get; init; }
        public int MaxHp { get; init; }
        public int Gold { get; init; }
        public int Floor { get; init; }
        public IReadOnlyList<string> Relics { get; init; } = Array.Empty<string>();
    }

    public sealed record DeckCard(string Id, int FloorAdded);

    /// <summary>
    /// Find the freshest current_run*.save across both modded and unmodded profiles,
    /// across SP and MP. Returns null if none exists.
    /// </summary>
    public static Deck? ReadFreshest()
    {
        var roots = EnumerateSaveDirs().ToList();
        var candidates = new List<FileInfo>();
        foreach (var dir in roots)
        {
            foreach (var name in new[] { "current_run.save", "current_run_mp.save" })
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path)) candidates.Add(new FileInfo(path));
            }
        }
        if (candidates.Count == 0) return null;

        // Newest by mtime wins — same heuristic the dev server uses.
        var newest = candidates.OrderByDescending(f => f.LastWriteTimeUtc).First();
        return ReadFile(newest.FullName);
    }

    public static Deck? ReadFile(string path)
    {
        if (!File.Exists(path)) return null;

        SaveJson? save;
        try
        {
            var text = File.ReadAllText(path);
            save = JsonSerializer.Deserialize<SaveJson>(text, JsonOpts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[SaveFileReader] Failed to parse {path}: {ex.Message}");
            return null;
        }

        if (save?.Players == null || save.Players.Count == 0) return null;
        var p = save.Players[0];

        return new Deck
        {
            SourcePath = path,
            Modified = File.GetLastWriteTime(path),
            CharacterId = p.CharacterId ?? "<unknown>",
            Cards = (p.Deck ?? new()).Select(c => new DeckCard(c.Id ?? "", c.FloorAddedToDeck)).ToList(),
            CurrentHp = p.CurrentHp,
            MaxHp = p.MaxHp,
            Gold = p.Gold,
            Floor = save.CurrentActIndex, // best proxy without parsing acts
            Relics = (p.Relics ?? new()).Select(r => r.Id ?? "").ToList(),
        };
    }

    private static IEnumerable<string> EnumerateSaveDirs()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var steamRoot = Path.Combine(appData, "SlayTheSpire2", "steam");
        if (!Directory.Exists(steamRoot)) yield break;

        foreach (var steamId in Directory.EnumerateDirectories(steamRoot))
        {
            // Both modded/profile1/saves and profile1/saves can exist.
            var moddedSaves = Path.Combine(steamId, "modded", "profile1", "saves");
            var unmoddedSaves = Path.Combine(steamId, "profile1", "saves");
            if (Directory.Exists(moddedSaves)) yield return moddedSaves;
            if (Directory.Exists(unmoddedSaves)) yield return unmoddedSaves;
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed class SaveJson
    {
        [JsonPropertyName("players")] public List<PlayerJson>? Players { get; set; }
        [JsonPropertyName("current_act_index")] public int CurrentActIndex { get; set; }
    }

    private sealed class PlayerJson
    {
        [JsonPropertyName("character_id")] public string? CharacterId { get; set; }
        [JsonPropertyName("deck")] public List<CardJson>? Deck { get; set; }
        [JsonPropertyName("relics")] public List<RelicJson>? Relics { get; set; }
        [JsonPropertyName("current_hp")] public int CurrentHp { get; set; }
        [JsonPropertyName("max_hp")] public int MaxHp { get; set; }
        [JsonPropertyName("gold")] public int Gold { get; set; }
    }

    private sealed class CardJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("floor_added_to_deck")] public int FloorAddedToDeck { get; set; }
    }

    private sealed class RelicJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
