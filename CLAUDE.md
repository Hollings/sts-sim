# CLAUDE.md — StS2Sim

This file is for Claude Code (claude.ai/code) when working in `StS2Sim/`.
Read this before changing the simulator.

## What this is

A headless Slay the Spire 2 deck simulator that runs the **actual game DLL** (`sts2.dll`)
outside the Godot engine. Used for damage-per-turn analysis and "is card X worth adding
to deck Y" A/B testing — without playing the game by hand a thousand times.

Not a reimplementation. We deliberately use real game logic so the sim stays in sync
with patches: when MegaCrit changes Strike to 7 damage, the numbers update with no
code changes.

## Build & run

```bash
cd StS2Sim
dotnet run -c Release -p:STS2GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

Default mode: bootstraps + starts the embedded HTTP server on port 52324
and opens the browser. The UI reads the freshest `current_run.save`,
shows your deck, and lets you run sim batches with live-streaming charts.

Console-only experiment mode (no UI):
```bash
dotnet run -c Release -- experiment
```
Runs the legacy headline A/B + K-vs-accuracy console output.

The exe is fully standalone — no mod required, no Python, no game running.
Just needs a `current_run.save` somewhere under `%APPDATA%\SlayTheSpire2\`.

## How the bootstrap works

The game DLL was built for a Godot host. We trick it into running outside one. **Five
moving pieces, in this order:**

1. **`TestMode.IsOn = true`** flips `NonInteractiveMode.IsActive`, which short-circuits
   every `Cmd.Wait` / `Cmd.CustomScaledWait` / `SfxCmd` / VFX-spawning call site in the
   codebase via `if (!NonInteractiveMode.IsActive && ...)` guards. No SceneTree timers
   needed. This is the foundation — without it, the very first damage call would
   `await` on a Godot timer that doesn't exist and hang forever.

2. **Harmony shims for native interop** (`GodotShims.cs`). A handful of `Godot.*` calls
   crash with 0xC0000005 because they P/Invoke into a native runtime we never loaded.
   Patches:
   - `Logger.GetIsRunningFromGodotEditor` → return false (skip `OS.GetCmdlineArgs`)
   - `ConsoleLogPrinter.Print` → `Console.WriteLine` (skip `GD.Print`)
   - `Godot.Time.GetTicksMsec` → return 0 (used for animation duration math; collapses
     to a 0-duration wait which is no-op anyway under NonInteractiveMode)
   - `Creature.ToString` → bypass localization (LocString hits null)

3. **`ModelDb.Init()` + `ModelIdSerializationCache.Init()` + `ModelDb.InitIds()`**
   directly. The full `OneTimeInitialization.ExecuteEssential()` path calls Godot's
   atlas/locale loaders which need a running engine; these three functions are the
   only ones we actually need (registers all 1611 game models).

4. **Reflection to set `CombatManager.Instance._state` and `IsInProgress = true`.**
   The official path is `CombatManager.SetUpCombat(state)` which does too much
   (`NetCombatCardDb.StartCombat`, RNG-driven shuffles, GodotEvent invocations).
   We just poke the two fields the damage pipeline checks.

5. **`combatState.AddPlayer(player)` + `combat.CreateCreature(monster, side)` +
   `combat.AddCreature(creature)`.** `CreateCreature` attaches the creature but
   does NOT add it to `_enemies`/`_allies` — that's `CombatManager.SetUpCombat`'s job.
   We do it manually.

All wrapped in `Harness.Bootstrap()` (one-time) + `Harness.BeginCombat<TCharacter>()`
(per trial). See `Harness.cs:31`.

## Project layout

| File | Role |
|---|---|
| `Program.cs` | Entry point. Installs `AssemblyLoadContext.Resolving` to find game DLLs by name from `STS2_GAME_DIR`, then applies Harmony shims. |
| `GodotShims.cs` | The Harmony patches that make the game DLL safe to call without a SceneTree (logger, Time, AutoPlay capture hook, Shuffle replacement, Creature.ToString loc shim). Owns the patch wiring; capture state lives in `PlayCapture.cs`. |
| `PlayCapture.cs` | Thread-static per-turn list of card plays. The autoplay Harmony prefix records here; `DamagePerTurnSim` drives Start/Stop around each turn. |
| `Harness.cs` | `Bootstrap()` (init ModelDb etc) and `BeginCombat<T>(deckOverride, shuffleSeed)` / `EndCombat()`. The reusable per-trial setup. Exposes nested `DeckEntry` / `CombatHarness` types. |
| `Reflect.cs` | Single home for the private-member pokes we need (`CombatManager._state` / `IsInProgress`, `PlayerCombatState.Energy` setter, `Creature.CurrentHp` setter). MethodInfo cached at startup. |
| `CardLabels.cs` | Display formatting: `"CARD.STRIKE_IRONCLAD"` + level → `"Strike Ironclad+"`. Single source of truth used by everywhere that renders a card name. |
| `TurnHooks.cs` | `FireAfterTurnEnd(harness, side)` — manual listener iteration for end-of-turn power ticks (the official `Hook.AfterTurnEnd` requires `LocalContext.NetId` we don't have). |
| `SmokeTests.cs` | Assertion-based tests: Strike=6, Bash applies Vulnerable, Inflame +Strength → Strike does 8, etc. **Run these first if anything seems off.** |
| `DamagePerTurnSim.cs` | One trial = run N turns of "fill hand → play cards via policy → end turn". `RunSingleTrial(seed)` is the brick. Per-turn body is `RunSingleTurn`; play loop is `PlayPhase`. |
| `Policies/IPlayPolicy.cs` + `Policies/*.cs` | One file per policy: `GreedyAttackPolicy`, `HighestDamagePolicy`, `RandomPolicy`, `EpsilonGreedyPolicy(base, ε)`. New policies go here. |
| `BestOfKRunner.cs` | The recommended algorithm. Per-seed: K samples, keep max. Average those across N seeds. Reports `avg-of-best ± 95% CI`. |
| `Sim/ConvergenceRunner.cs` | Console-only. Anytime mode for debugging policy behavior. Not used by the web UI. |
| `Sim/ExperimentMode.cs` | Console-only. Wires up the K-vs-accuracy curve + Defend-for-Inflame swap A/B. Runs only via `dotnet run -- experiment`. |
| `SaveFileReader.cs` | Walks `%APPDATA%\SlayTheSpire2\steam\<steamid>\{,modded/}profile1\saves\current_run*.save`, picks freshest by mtime, parses player[0].deck. Pure file IO + System.Text.Json — no game state needed. |
| `CardIdResolver.cs` | `"CARD.STRIKE_IRONCLAD"` → `typeof(StrikeIronclad)` via `ModelDb.GetByIdOrNull`. Requires `Harness.Bootstrap()` first. (Display formatting is in `CardLabels.cs`.) |
| `Server/SimServer.cs` | `HttpListener` on :52324 with WebSocket. Routes: `GET /` (static), `GET /api/deck`, `POST /api/sim/start`, `POST /api/sim/stop`, `WS /ws`. Owns transport + WS fan-out only. |
| `Server/SimJob.cs` | One end-to-end best-of-K run with progress events shaped for the UI. Wire-shape contract: event `type` strings + field names match `www/app.js`. |
| `AutoCardSelector.cs` | Global "auto-pick" selector for cards like Armaments / Havoc that wait on a `CardSelectCmd`. |
| `www/index.html` + `www/app.js` | Single-page UI. Plain JS + Chart.js (CDN). Three charts: per-seed scatter, running avg with 95% CI band, damage histogram. StS2 color theme (`#183749` bg, `#f2f0c4` fg, `#8b1913` accent). |

## The algorithm we settled on

**Best-of-K with ε-greedy base policy, per-seed averaging, two-sample z-test for verdicts.**

```
For each shuffle seed s in N seeds:
    For each k in K samples:
        Play with policy = "ε of the time random, otherwise highest-damage"
        Record total damage
    Record max(damages) for seed s
metric = mean(per_seed_maxes)
ci95   = 1.96 × stderr(per_seed_maxes)

For deck A vs deck B comparison:
    Run above for both with same seeds + same policy
    diff = B.metric - A.metric
    z    = diff / sqrt(A.stderr² + B.stderr²)
    verdict = z>2 → "ADD IT", z<-2 → "REMOVE", else "INCONCLUSIVE"
```

**Why this and not other things:**

- **ε-greedy not pure greedy** because pure greedy can't see setup cards (Inflame deals
  0 dmg — greedy never plays it; ε-random plays it 1-in-N times and best-of-K finds the
  payoff). On a deck with 2 Inflames, pure greedy reports 102 damage vs random's 140.
- **ε-greedy not pure random** because random wastes ~50% of plays on Defends and bad
  orderings. Doesn't converge in reasonable K on simple decks.
- **Best-of-K not just-mean** because we want the *ceiling* under near-optimal play, not
  the average random-noise outcome. Maxing per seed eliminates play-decision variance
  while keeping shuffle variance intact.
- **Per-seed averaging not best-ever** because best-ever measures luckiest shuffle, not
  deck quality. Useless for comparison.
- **Z-test on diff, not "does best go up"** because a 1-dmg lift can be noise; ±CI
  tells us if the lift is real.
- **Not MCTS** because MCTS solves "find optimal play for one fixed seed" — not what
  deck comparison asks. ε-greedy avoids MCTS's lock-in concern via uniform exploration.

## Default knobs and what they mean

| Knob | Default | Range | Effect |
|---|---|---|---|
| `ε` (epsilon) | 0.30 | 0.10–0.50 | Higher = better at finding setup-card plays, more noise per sample. 0.30 works for both pure-damage and setup-heavy decks. |
| `K` (samples per seed) | 30–50 | 10–300 | Higher = tighter CI **and** higher mean (low K systematically underestimates setup decks). Diminishing returns past ~100. |
| `seeds` | 200–500 | 100–2000 | Higher = tighter CI on overall metric. 500 gives ±1 dmg/5-turn confidence. |
| `Turns` | 5 | 3–10 | Setup cards become more valuable with more turns. 5 is a reasonable mid-game encounter length. |

Throughput: ~1000 trials/sec single-threaded → 25k runs ≈ 25 sec per deck. A full
A/B test is ~1 minute.

## Phase 1 vs Phase 2 status

**Phase 1: state machine — DONE.** Card piles (hand, draw, discard, exhaust), draw, exhaust, multi-hit,
self-damage, energy, block. All verified by `SmokeTests.cs`.

**Phase 2: modifier pipeline — DONE.** `IterateHookListeners` is enabled (no longer short-circuited).
Powers apply via `PowerCmd.Apply`, modify damage via `Hook.ModifyDamage`, tick down via
`AfterTurnEnd`. Verified: Bash applies Vulnerable, next Strike does 9 (6×1.5); Inflame
applies +2 Strength, next Strike does 8 (6+2).

**Phase 3 ideas (not started):**
- Real enemy turns (currently BigDummy is a no-op). Needs `MonsterMoveStateMachine` wiring,
  attack intents, etc.
- Block valuation in the "deck quality" metric (a deck that survives is worth more than
  a damage-equal one that dies).
- Energy gain/loss outside of `Bloodletting` style (Adrenaline, etc.) — should already
  work but untested.
- Multi-target attacks (currently only BigDummy-as-single-enemy is verified).
- End-of-combat triggers (Burning Blood heal, Feed +max HP).

## Known limits / footguns

- **`CombatManager.Instance` is a singleton.** Cannot run two combats in parallel within
  one process without solving this. Subprocess or `AssemblyLoadContext` parallelism if
  we need throughput.
- **`Hook.AfterTurnEnd` requires `LocalContext.NetId` to be set, or it returns early.**
  We sidestep by manually iterating `state.IterateHookListeners()` and calling
  `listener.AfterTurnEnd()` directly — see `DamagePerTurnSim.FireAfterTurnEnd`.
- **Discard→draw reshuffle in our DPT loop is NOT seeded by `RunState.Rng.Shuffle`** like
  the real game does. It's accidentally deterministic (iteration order) but not realistic.
  If we ever need realism here, route through `CardPileCmd.Shuffle` or seed our own.
- **Some seeds were still improving at K=50.** The "→ Some seeds were still improving"
  warning means the K cap is biting and the metric is biased low. Bump K if accuracy
  matters more than speed.
- **Power application uses `PowerCmd.Apply` which fires the full Hook chain.** Works,
  but means our hook short-circuit shim from Phase 1 (`IterateHookListeners` returning
  empty) is GONE. Removing/re-adding it without breaking power damage is non-trivial.
- **Energy is set via reflection on `PlayerCombatState.Energy`** (private setter).
  See `SmokeTests.SetEnergy`.

## Things we tested and ruled out

| Idea | Why we rejected it |
|---|---|
| Pure greedy as default | Blind to setup cards (Inflame, Limit Break, Demon Form). Biased low for any deck with non-immediate-damage cards. |
| Pure random as default | Too noisy. K=200 still below greedy's accuracy on simple decks. |
| "Best ever damage" as deck quality metric | Measures luckiest shuffle, not deck quality. All policies hit the same max within seconds — no discrimination. |
| MCTS | Solves "best play for fixed seed" — not the deck-comparison question. Lock-in concern is real but ε-greedy sidesteps it via uniform exploration. Worth revisiting if we ever do TAS-style "optimal play for known seed". |
| Bigger K instead of better policy | Marginal gains past K=100 on simple decks; can't escape the bias from pure-random base on setup decks. |

## Useful invariants

- Adding a card to a `CardPile` requires `card.Owner = player` first. `Pile` is a
  derived property: `_owner?.Piles.FirstOrDefault(p => p.Cards.Contains(this))`.
  Cards without an owner have `Pile == null` and `OnPlayWrapper` returns early
  with no error.
- `card.CombatState` is also derived: returns `_owner?.Creature.CombatState` only when
  the card is in a combat pile (Hand, Draw, Discard, Play, Exhaust). Cards in the
  permanent `Player.Deck` pile return `null`.
- `Creature.IsAlive` ≡ `CurrentHp > 0`. Player's `IsActiveForHooks` is set in the ctor
  based on `Creature.IsAlive`. If you somehow set `CurrentHp` to 0, hooks for that
  player stop firing.
- `MonsterModel.MutableClone` ≠ `MonsterModel`. Always `.ToMutable()` before passing to
  `combat.CreateCreature`. Same for cards.
- The DPT loop heals the dummy back to full each turn — combat never ends. Because we
  never tick `IsEnding` to true, the engine just keeps running.

## What to build next (roughly in order of value)

1. **Card-swap UI**: in the web frontend, click a card in the deck to remove,
   pick another to add, run an A/B against current. The actual "should I add
   this card?" feature.
2. **Adaptive K**: stop sampling per-seed when CI < threshold instead of fixed K.
   Saves 5-10x compute on easy verdicts.
3. **Smarter base policy**: hand-coded heuristics like "play Powers turn 1",
   "apply Vulnerable before attacking". Could shrink ε-needed and tighten CI.
4. **Subprocess parallelism**: ~6x throughput. Don't bother until #1-3 feel slow.
5. **Phase 3**: real enemy turns. Required for any "did the player survive" question.

## UI notes (frontend)

- WebSocket auto-reconnects on disconnect (1-second backoff).
- Charts throttle updates to every 3rd seed event for big runs to keep the
  browser responsive. Final update happens on `done`.
- The "freshest save wins" rule means if the user has both modded and unmodded
  profiles, whichever was last touched is what we read. Could add a profile
  picker later, but the typical user only plays one mode at a time.
- Card IDs in the save file (e.g. `CARD.STRIKE_IRONCLAD`) match `card.Id`
  from the game DLL exactly. If we ever see an "unknown card id" error,
  either the game added a new card or our `ModelDb.Init` didn't cover it.
