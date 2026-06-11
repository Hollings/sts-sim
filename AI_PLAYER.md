# AI Player — Architecture

The end goal: a full end-to-end AI player for Slay the Spire 2. It is built
from three **independent** components that talk only over local HTTP. None of
them references another's code; each is useful alone.

```
┌────────────────────┐      ┌──────────────────┐      ┌─────────────────────────┐
│ snecko-eye (mod)   │      │ driver           │      │ StS2Sim (this repo)     │
│ in the live game   │ HTTP │ orchestration    │ HTTP │ headless evaluation     │
│                    │◄────►│ loop             │◄────►│                         │
│ GET  /state        │      │ (separate        │      │ POST /api/advise/combat │
│ POST /action       │      │  project)        │      │ POST /api/sim/start     │
│ :9000              │      │                  │      │ :52324                  │
└────────────────────┘      └──────────────────┘      └─────────────────────────┘
     eyes + hands                 the loop                     the brain
```

- **snecko-eye** (separate repo, `slaythespirearchi/snecko-eye`): a mod inside
  the running game exposing full game state and accepting actions for every
  phase (combat, map, events, shops, rewards, rest, relics). It knows nothing
  about the sim.
- **StS2Sim** (this repo): the headless simulator. It knows nothing about
  snecko-eye or the live game process — it accepts a *combat state described
  as JSON* and evaluates it by actually playing it out on the real game DLL.
- **driver**: the agent loop. Polls state, asks the sim for advice where the
  sim is competent, applies heuristics elsewhere, sends actions. Not yet
  built; it belongs next to snecko-eye (a Python loop alongside `sts2.py`) or
  as its own small project — NOT inside either of the other two.

## What StS2Sim contributes (and what it deliberately doesn't)

The sim's job is **evaluation**, exposed as API:

| Decision | Endpoint | Status |
|---|---|---|
| Which card do I play right now / do I end turn? | `POST /api/advise/combat` | built (this doc) |
| Which card reward do I take (or skip)? | `POST /api/sim/start` with `candidates` | already existed (compare mode) |
| Is deck edit X an improvement? | `POST /api/sim/start` with `removals`/`additions` | already existed (A/B mode) |

Not the sim's job: map pathing, event choices, shop strategy, potion timing,
when to rest vs smith. Those are driver policy (heuristics or LLM), possibly
*informed* by sim calls ("win rate of current deck vs this act's pool"), but
the logic lives in the driver.

## POST /api/advise/combat

**Request** — a JSON description of an in-progress combat. The schema is
owned by this repo (`src/Advise/AdviseRequest.cs`); it is deliberately
field-compatible with snecko-eye's `GET /state` output so a driver can pipe
one into the other, but anything that can produce this shape can use the
endpoint:

```jsonc
{
  "run":    { "character": "CHARACTER.IRONCLAD" },
  "player": { "hp": 64, "max_hp": 80, "block": 0 },
  "relics": [ { "id": "RELIC.BURNING_BLOOD" } ],
  "deck":   [ { "id": "CARD.STRIKE_IRONCLAD", "upgraded": false }, ... ],
  "combat": {
    "turn": 2,
    "energy": 3,
    "hand": [
      { "hand_index": 0, "uid": "strike_1", "id": "CARD.STRIKE_IRONCLAD",
        "upgraded": false, "playable": true, "target_type": "AnyEnemy" }, ...
    ],
    "draw_pile_count": 5,
    "discard_pile": [ { "id": "CARD.BASH", "upgraded": false } ],
    "exhaust_pile": [],
    "player_powers": [ { "id": "POWER.STRENGTH", "stacks": 2 } ],
    "enemies": [
      { "enemy_index": 0, "id": "MONSTER.JAW_WORM", "hp": 30, "max_hp": 44,
        "block": 0, "is_alive": true, "intent": "CHOMP_MOVE",
        "powers": [] }
    ]
  }
}
```

Query options: `?seeds=12&horizon=8` (rollouts per candidate; max additional
turns before scoring a cap-out as a loss).

**Response** — every legal action ranked by average rollout outcome:

```jsonc
{
  "actions": [
    { "type": "play_card", "cardUid": "bash_1", "handIndex": 2, "targetIndex": 0,
      "label": "Bash → Jaw Worm", "avgScore": 41.2, "winRate": 1.0, "rollouts": 12 },
    { "type": "play_card", "cardUid": "strike_1", ... },
    { "type": "end_turn", "label": "end turn", "avgScore": 28.0, "winRate": 0.92, ... }
  ],
  "mirror": { "drawInferred": 5, "drawReported": 5, "notes": [] }
}
```

Scores use the same outcome scalar as fight sims: win → +player HP kept,
loss/horizon-cap → −(living enemy HP). The driver plays the top action,
re-polls state, asks again — one advise call per decision, not per turn.

## How the mirror works (src/Advise/StateMirror.cs)

The advisor reconstructs the live combat inside the harness, then rolls it
forward with the real game rules:

1. **Character + relics + deck**: `Harness.BeginCombat` with the character
   resolved from `run.character`, the relic list, and a deck override equal to
   the combat's card multiset. The draw pile's *contents* are inferred as
   `deck − hand − discard − exhaust` (the live API exposes only its count);
   its *order* is unknown and is exactly what the per-seed shuffles sample.
2. **Piles**: listed hand/discard/exhaust cards are moved to their piles by
   (card id, upgrade); the remainder stays in the draw pile.
3. **Creatures**: enemies are built from `MONSTER.*` ids (new `monsterIds`
   mode in `Harness.BeginCombat` — same plumbing as encounter mode, minus the
   encounter). HP / max HP / block are forced to live values on both sides.
4. **Powers**: applied via the game's own `PowerCmd.Apply`, then the stack
   amount is forced to the exact live value (apply-time hooks could otherwise
   modify it).
5. **Intents**: each enemy's known intent (`intent` move id) is forced via
   the game's `MonsterModel.SetMoveImmediate(state, forceTransition: true)` —
   so turn-1 rollouts face exactly the move the player is looking at. Later
   turns roll from each trial's seeded RNG, sampling the future honestly.
6. **Rollout**: candidate action first (or nothing, for `end_turn`), then the
   race policy finishes the turn, then full turns alternate exactly like
   `EncounterSim` until win / death / horizon.

### Known fidelity gaps (documented, not hidden)

- **Mid-combat-generated cards in the draw pile** can't be inferred (only the
  pile count is exposed). The mirror reports the count mismatch in
  `mirror.drawInferred/drawReported` so the driver can see when fidelity drops.
- **Osty / pets are not in the state schema yet** — Necrobinder mirrors start
  pet-less (Bound Phylactery resummons from round 2 in rollouts, softening
  this). Needs an `allies` block in the schema (and in snecko-eye) eventually.
- **Stars (Regent) are not in the schema yet** — same story; mirrors start at
  the live energy but 0 stars.
- **Power internal state** beyond stack count (e.g. a power that remembered
  something from two turns ago) is not reconstructed.
- **Card-level mutations** (Claw/Kingly Punch grown damage, Forged blades)
  reset to base; the schema would need per-card current values.
- The play policy inside rollouts is the benchmarked race policy — the same
  fair-comparator caveats as fight sims apply.

## Driver sketch (for when it gets built — NOT in this repo)

```python
while True:
    s = snecko.state()
    match s["phase"]:
        case "combat_player_turn":
            best = sim.advise_combat(s)["actions"][0]
            snecko.action(best)            # play_card / end_turn verbatim
        case "awaiting_card_selection" if reward:
            pick = sim.compare(deck, candidates, encounter=next_boss)
            snecko.action(select(pick))
        case "map_selection" | "event" | ...:
            heuristics(s)                  # driver policy, not sim
```

## Build order

1. ✅ `/api/advise/combat` — mirror + per-action rollouts (this doc).
2. Driver v1 next to snecko-eye: combat + rewards via the sim, everything
   else simple heuristics. Goal: complete runs unattended.
3. Schema v2: allies/Osty, stars, per-card mutated values — extend the
   advise schema first, then snecko-eye's reader, then the mirror.
4. Driver v2: map pathing informed by sim win rates, event policy table.
