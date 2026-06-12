using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace StS2Sim.Advise;

/// <summary>
/// The combat-state schema accepted by <c>POST /api/advise/combat</c>. This
/// is StS2Sim's OWN contract (see AI_PLAYER.md): it is deliberately
/// field-compatible with the snecko-eye mod's <c>GET /state</c> response so a
/// driver can pipe one into the other, but the sim has no dependency on that
/// mod — anything able to produce this JSON can ask for advice. Unknown
/// fields in the request are ignored.
/// </summary>
public sealed class AdviseRequest
{
    [JsonPropertyName("run")] public RunBlock? Run { get; set; }
    [JsonPropertyName("player")] public PlayerBlock? Player { get; set; }
    [JsonPropertyName("relics")] public List<RelicBlock>? Relics { get; set; }
    [JsonPropertyName("deck")] public List<CardRef>? Deck { get; set; }
    [JsonPropertyName("combat")] public CombatBlock? Combat { get; set; }

    public sealed class RunBlock
    {
        [JsonPropertyName("character")] public string? Character { get; set; }
    }

    public sealed class PlayerBlock
    {
        [JsonPropertyName("hp")] public int Hp { get; set; }
        [JsonPropertyName("max_hp")] public int MaxHp { get; set; }
        [JsonPropertyName("block")] public int Block { get; set; }
    }

    public sealed class RelicBlock
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }

    public sealed class CardRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("upgraded")] public bool Upgraded { get; set; }
        /// <summary>Optional card enchantment (Instinct, Sharp, ...). Not yet
        /// emitted by snecko-eye — when absent the card mirrors unenchanted.</summary>
        [JsonPropertyName("enchantment")] public EnchantRef? Enchantment { get; set; }
    }

    public sealed class EnchantRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("amount")] public int Amount { get; set; }
    }

    public sealed class CombatBlock
    {
        [JsonPropertyName("turn")] public int Turn { get; set; } = 1;
        [JsonPropertyName("energy")] public int Energy { get; set; }
        [JsonPropertyName("hand")] public List<HandCard> Hand { get; set; } = new();
        [JsonPropertyName("draw_pile_count")] public int DrawPileCount { get; set; }
        [JsonPropertyName("discard_pile")] public List<CardRef> DiscardPile { get; set; } = new();
        [JsonPropertyName("exhaust_pile")] public List<CardRef> ExhaustPile { get; set; } = new();
        [JsonPropertyName("player_powers")] public List<PowerRef> PlayerPowers { get; set; } = new();
        [JsonPropertyName("enemies")] public List<EnemyBlock> Enemies { get; set; } = new();
    }

    public sealed class HandCard
    {
        [JsonPropertyName("hand_index")] public int HandIndex { get; set; }
        [JsonPropertyName("uid")] public string? Uid { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("upgraded")] public bool Upgraded { get; set; }
        [JsonPropertyName("playable")] public bool Playable { get; set; } = true;
        [JsonPropertyName("target_type")] public string? TargetType { get; set; }
        [JsonPropertyName("enchantment")] public EnchantRef? Enchantment { get; set; }
    }

    public sealed class PowerRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("stacks")] public decimal Stacks { get; set; }
    }

    public sealed class EnemyBlock
    {
        [JsonPropertyName("enemy_index")] public int EnemyIndex { get; set; }
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("hp")] public int Hp { get; set; }
        [JsonPropertyName("max_hp")] public int MaxHp { get; set; }
        [JsonPropertyName("block")] public int Block { get; set; }
        [JsonPropertyName("is_alive")] public bool IsAlive { get; set; } = true;
        /// <summary>The move id the enemy is telegraphing (snecko-eye's
        /// <c>intent</c> field). When present, turn-1 rollouts face exactly
        /// this move; when absent, the move is rolled per trial seed.</summary>
        [JsonPropertyName("intent")] public string? Intent { get; set; }
        [JsonPropertyName("powers")] public List<PowerRef> Powers { get; set; } = new();
    }
}
