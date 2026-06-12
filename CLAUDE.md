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

Console-only modes (no UI):
```bash
dotnet run -c Release -- experiment       # legacy headline A/B + K-vs-accuracy console output
dotnet run -c Release -- smoke            # the 15 Ironclad assertion tests, fast
dotnet run -c Release -- silent-tests     # the 174-test Silent card battery; exit 2 on crashes
dotnet run -c Release -- encounter-sweep  # 1 short fight vs EVERY encounter; exit 2 on crashes
dotnet run -c Release -- character-sweep  # starter-deck trial per character (dummy + fight); exit 2 on crashes
dotnet run -c Release -- char-tests       # 45-test Regent/Necrobinder/Defect mechanics battery; exit 2 on crashes
dotnet run -c Release -- card-sweep       # play EVERY card once (base + upgraded, ~1040 plays); exit 2 on crash/hang
dotnet run -c Release -- advise-test      # combat-advisor smoke (live-state mirror + rollouts); exit 2 on failure
dotnet run -c Release -- policy-bench     # play-policy uplift benchmark on a pinned suite
```

The server also exposes `POST /api/advise/combat` — give it a JSON snapshot
of an in-progress combat and it returns every legal action ranked by rollout
outcome. This is the sim's contribution to the end-to-end AI player; the
architecture, request schema, and fidelity caveats live in **AI_PLAYER.md**.
The schema is field-compatible with the snecko-eye mod's `GET /state`, but
the sim has no dependency on that mod — keep it that way.

All five characters (Ironclad, Silent, Regent, Necrobinder, Defect) run in
both modes — verified by `character-sweep`. Per-card assertion coverage:
Ironclad (smoke, 15), Silent (174-test battery, the deepest), and the
`char-tests` battery for the other three — targeted at the fragile character
mechanics rather than full pools: Defect orbs (channel/evoke/passives/Focus/
slots/X-cost MultiCast), Necrobinder's Osty (summon/stack/whiff/IsPlayable
gate/Souls/Sacrifice/history-driven Rattle), Regent stars (SpendResources
debits, Forge/SovereignBlade, draw-scaling Kingly Punch, history-driven
Radiate) plus all three starter relics.

The exe is fully standalone — no mod required, no Python, no game running.
Just needs a `current_run.save` somewhere under `%APPDATA%\SlayTheSpire2\`.

Distribution: `.\publish.ps1` → `dist\StS2Sim-win64.zip` (~35 MB,
self-contained .NET, game DLLs resolved from the target's Steam install at
runtime — never bundled). Game dir discovery order: `STS2_GAME_DIR` env →
default Steam paths → Steam registry + `libraryfolders.vdf` (all drives).
Fatal startup errors hold the console window open so double-click users can
read them.

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
| `SmokeTests.cs` | Assertion-based tests: Strike=6, Bash applies Vulnerable, Inflame +Strength → Strike does 8, etc. **Run these first if anything seems off** (`dotnet run -- smoke`). |
| `DamagePerTurnSim.cs` | One trial = run N turns of "fill hand → play cards via policy → end turn". `RunSingleTrial(seed)` is the brick. Per-turn body is `RunSingleTurn`; play loop is `PlayPhase`, which debits via the game's own `card.SpendResources()` (X-cost capture, 0-cost-at-0-energy, AfterEnergySpent hook all correct). |
| `Policies/IPlayPolicy.cs` + `Policies/*.cs` | One file per policy: `GreedyAttackPolicy`, `HighestDamagePolicy`, `RandomPolicy`, `EpsilonGreedyPolicy(base, ε)`, `ThresholdPolicy(t)` (race until incoming damage would drop HP below t×maxHP, then stack block — t<0 never defends, ∞ turtles). `Playable` is two-tier: cheap screen (Unplayable keyword + cost) over the whole hand, the game's full `CanPlay()` (hook listeners) only on the final pick via `ChooseFrom` — calling CanPlay per hand card per pick cost ~3x throughput. |
| `IntentReader.cs` | Telegraphed incoming damage for the current round, read the same way the intent UI does (NextMove.Intents) but with Hook.ModifyDamage run against OUR player — the game's own path resolves the player via netcode and silently falls back to base damage headless. |
| `PolicyBench.cs` | Policy uplift benchmark (`dotnet run -- policy-bench`): pinned decks × encounters, race-only vs candidate policy setups on identical seeds, win-rate/score deltas + per-personality seed attribution. Win rate is a lower bound, so any uplift on same seeds = strictly more accurate. Run this before changing any play policy default. |
| `BestOfKRunner.cs` | The recommended algorithm. Per-seed: K samples, keep max. Average those across N seeds. Reports `avg-of-best ± 95% CI`. `Patience > 0` early-stops a seed after that many samples without improvement. `Summary.PerSeedBests` enables paired A/B tests. |
| `CardCatalog.cs` | Character pool + colorless pool with display metadata — backs the UI's "add a card" picker (`GET /api/cards`). |
| `Sim/ConvergenceRunner.cs` | Console-only. Anytime mode for debugging policy behavior. Not used by the web UI. |
| `Sim/ExperimentMode.cs` | Console-only. Wires up the K-vs-accuracy curve + Defend-for-Inflame swap A/B. Runs only via `dotnet run -- experiment`. |
| `SaveFileReader.cs` | Walks `%APPDATA%\SlayTheSpire2\steam\<steamid>\{,modded/}profile1\saves\current_run*.save`, picks freshest by mtime, parses player[0].deck. Pure file IO + System.Text.Json — no game state needed. |
| `CardIdResolver.cs` | `"CARD.STRIKE_IRONCLAD"` → `typeof(StrikeIronclad)` via `ModelDb.GetByIdOrNull`. Requires `Harness.Bootstrap()` first. (Display formatting is in `CardLabels.cs`.) |
| `Server/SimServer.cs` | `HttpListener` on :52324 with WebSocket. Routes: `GET /` (static, confined to webroot), `GET /api/deck`, `GET /api/cards`, `POST /api/sim/start` (accepts `removals`/`additions` for A/B), `POST /api/sim/stop`, `WS /ws`. One job at a time: a new start cancels and awaits the old job under `_jobGate` so events never interleave. |
| `Server/SimJob.cs` | One end-to-end best-of-K run with progress events shaped for the UI. With `VariantDeck` set it runs baseline then variant on identical shuffle seeds and finishes with a **paired z-test** verdict (`abDone` event) — pairing cancels shuffle luck, so ~40 seeds give the discrimination the old unpaired console flow needed 500 for. With `Candidates` set (the card-reward question) it runs baseline + one phase per candidate, ranks them by paired lift, runs a winner-vs-runner-up paired test, and emits `compareDone` with a TAKE/SKIP/toss-up verdict. Wire-shape contract: event `type` strings + field names match `www/app.js`. |
| `AutoCardSelector.cs` | Global "auto-pick" selector for cards like Armaments / Havoc that wait on a `CardSelectCmd`. |
| `www/index.html` + `www/app.js` | Single-page UI. Plain JS + Chart.js (CDN). Deck editor (click a card to mark a copy for removal, datalist picker to add cards) drives the A/B flow; the "Compare Candidates" picker drives compare mode. Charts have dynamic per-phase datasets (1 in single mode, 2 in A/B, 1+N in compare; `PHASE_COLORS` palette, baseline always blue). Verdict panel renders the `abDone` paired test or the `compareDone` ranking table. Config fields persist in localStorage. StS2 color theme (`#183749` bg, `#f2f0c4` fg, `#8b1913` accent). |

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

For deck A vs deck B comparison (SimJob A/B path — paired):
    Run above for both with the SAME seed sequence + same policy
    diff_i = B.perSeedBest[i] - A.perSeedBest[i]      # same shuffle on both sides
    z      = mean(diff) / stderr(diff)
    verdict = z>1.96 → "ADD IT", z<-1.96 → "DON'T", else "INCONCLUSIVE"
```

The paired test differences out shuffle luck, so its CI is far tighter than the
unpaired two-sample z (which `ExperimentMode.DeckAvsB` still uses): ~40 paired
seeds discriminate what ~500 unpaired seeds did.

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
| `seeds` | 200–500 | 100–2000 | Higher = tighter CI on overall metric. 500 gives ±1 dmg/5-turn confidence. Paired A/B needs far fewer (~100–200). |
| `Turns` | 5 | 3–10 | Setup cards become more valuable with more turns. 5 is a reasonable mid-game encounter length. |
| `Patience` | 12 (UI default) | 0–50 | Early-stop a seed's inner K loop after this many samples without a new best. 0 = always run all K. Cuts compute 2-4x; identical settings on both A/B sides cancel the slight low bias. |

Throughput: ~550-600 trials/sec single-threaded on a 14-card deck with relics
(measured June 2026). 200 seeds × K=30 with patience 12 ≈ 20s; a full A/B ≈ 40s.

## Phase 1 vs Phase 2 status

**Phase 1: state machine — DONE.** Card piles (hand, draw, discard, exhaust), draw, exhaust, multi-hit,
self-damage, energy, block. All verified by `SmokeTests.cs`.

**Phase 2: modifier pipeline — DONE.** `IterateHookListeners` is enabled (no longer short-circuited).
Powers apply via `PowerCmd.Apply`, modify damage via `Hook.ModifyDamage`, tick down via
`AfterTurnEnd`. Verified: Bash applies Vulnerable, next Strike does 9 (6×1.5); Inflame
applies +2 Strength, next Strike does 8 (6+2).

**Phase 3: real enemy turns — DONE (June 2026).** Pick any encounter (boss/elite/normal)
as the opponent and the sim runs the whole fight: monsters' `MonsterMoveStateMachine`
rolls and performs real moves through the game pipeline, the player takes damage,
block matters, minions spawn, and the trial ends on death or the turn cap.
Score = +player HP on win / −living primary-enemy HP on loss. `encounter-sweep`
verifies all 80 encounters run crash-free (~2s) — that's every act including
Underdocks, the alternate act-1 biome that ActModel.GetDefaultList() omits
(EncounterCatalog reads ModelDb.Acts for exactly this reason). Key pieces:
- `EncounterSim.cs` — the fight loop (player half-turn from DamagePerTurnSim's shared
  play phase + `TurnHooks.EnemyTurn`, which mirrors CombatManager's SwitchSides →
  enemy StartTurn → TakeTurn-per-enemy → EndEnemyTurnInternal flow).
- `Harness.BeginCombat(..., encounterId)` — generates the encounter's monster lineup
  (`EncounterModel.GenerateMonstersWithSlots`), calls `SetUpForCombat` per monster
  (move state machine), reseeds each monster's RunRngSet per trial (NullRunState.Rng
  mints an identical stream on every access — boss AI would be the same script every
  seed otherwise), and fires `AfterAddedToRoom` (Vantom's Slippery etc).
- `EncounterCatalog.cs` — act-grouped encounter list for `GET /api/encounters`.

**Phase 3 leftovers (not started):**
- Intent-aware play policy (block when the boss telegraphs a big hit). Currently
  ε-greedy finds block lines only by exploration, so survival is under-estimated.
- End-of-combat triggers (Burning Blood heal, Feed +max HP) — irrelevant to single
  fights, matters if we ever chain fights.

## Known limits / footguns

- **`CombatManager._pendingLoss` is sticky.** When the player dies the game calls
  `LoseCombat()`, and `AttackCommand` gates ALL damage on `IsOverOrEnding` — one
  lethal trial silently zeroes every later trial in the process. `Harness.EndCombat`
  clears it via reflection. If trials ever go "inert" (0 damage both ways), check
  this first.
- **UI singletons are stubbed, not absent.** `NGame.Instance` and
  `NCombatRoom.Instance` serve constructor-skipped instances with their
  screen-effect/visual members no-op'd (see GodotShims) because monster code
  dereferences them without null checks. `new NodePath(string)` is patched to skip
  native init — it hard-kills the process (0xC0000005) during argument evaluation
  otherwise. A new monster touching an unstubbed member shows up as a CRASH in
  `encounter-sweep`; a new CARD doing it shows up in `card-sweep` — run both
  after any game patch.
- **Anything touching netcode singletons headless dies or hangs.** Three found
  by `card-sweep`: (1) `CardSelectCmd.FromSimpleGrid` (Dredge) only consults
  the installed `ICardSelector` when `LocalContext.IsMe(player)` — false
  headless — and otherwise awaits a remote MP choice forever; shimmed to route
  straight to AutoCardSelector. (2) The co-op powers (Flanking, Covered,
  Knockdown, TagTeam) render the applier's player name via
  `RunManager.Instance.NetService` in `AfterApplied`; no-op'd (display-only —
  their gameplay lives in Modify* hooks which still run). (3) `PlayerCmd.EndTurn`
  (Void Form) drives the MP ready-up machinery; shimmed to set
  `GodotShims.EndTurnRequested`, which `RunPlayPhase` honors — no plays after
  Void Form, like the real game.
- **Multi-phase bosses need three pieces of machinery, all easy to miss.**
  Test Subject (AdaptablePower), Waterfall Giant (SteamEruptionPower), Axebot
  (StockPower), and the gremlin ambush (SurprisePower) all "die" mid-fight:
  (1) the win condition must consult `Hook.ShouldStopCombatFromEnding` — "no
  living primary enemy" alone scores phase 1 of a 3-phase boss as a WIN;
  (2) `Hook.AfterDeath` is NetId-gated and is where phase transitions, death
  reactions (Queen's amalgam enrage, Kin priest's last-follower response,
  crab-arm rage, on-death spawns), and Gremlin Horn live — de-gated in
  GodotShims like AfterDiedToDoom; (3) the enemy turn must run every creature
  still IN the combat state regardless of IsAlive (the game relies on
  death-removal to skip corpses; the corpses that remain are exactly the ones
  whose pending move is the phase respawn). All three are pinned by the
  "Boss mechanics" bucket in `char-tests`. `encounter-sweep <filter>` runs a
  single encounter with full crash stacks for debugging these.
- **Boss survival is a lower bound — but a tighter one than it looks.** The
  default policy races and never deliberately blocks. We benchmarked
  intent-aware blocking personalities (`policy-bench`) and they did NOT raise
  win rates — racing dominates with every deck tested, including a scaling
  deck. Defensive play only padded scores in already-decided fights. The
  remaining known gap is multi-turn burst telegraphs (Prepare → Dismember),
  which a one-turn intent read can't see.

- **`CombatManager.Instance` is a singleton.** Cannot run two combats in parallel within
  one process without solving this. Subprocess or `AssemblyLoadContext` parallelism if
  we need throughput.
- **`Hook.AfterTurnEnd` requires `LocalContext.NetId` to be set, or it returns early.**
  We sidestep by manually iterating `state.IterateHookListeners()` and calling
  `listener.AfterTurnEnd()` directly — see `DamagePerTurnSim.FireAfterTurnEnd`.
- **Orb passives and combat-start hooks are manual TurnHooks responsibilities.**
  The game fires `OrbQueue.BeforeTurnEnd` (lightning zap / frost block / dark
  accumulate) inside `CombatManager.DoTurnEnd`, `OrbQueue.AfterTurnStart` after
  the player side-turn-start hooks, and `Hook.BeforeCombatStart` (Bound
  Phylactery's initial Osty summon) before turn 1 — none of which our manual
  turn flow got for free. TurnHooks now mirrors all three; if a new character
  resource ticks "automatically" in the real game, suspect a missing
  CombatManager call here first. Also: `OrbCmd.AddSlots` dereferences
  `GetCreatureNode(...).OrbManager` with no null guard (unlike every other orb
  UI call) — shimmed in GodotShims with the capacity math minus the anim.
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
| Threshold-portfolio play policies for boss fights | Built and benchmarked June 2026 (`ThresholdPolicy` + `BestOfKRunner.Portfolio` + `policy-bench`). Hypothesis: splitting K across race/thr15/thr50/turtle personalities samples defensive lines deliberately instead of by ε-lottery. Result: FALSIFIED as a default. Defensive personalities only pad scores in already-lost fights; wherever wins are reachable, diluting the race policy's sample budget lowers win rates (scaling deck vs Ceremonial Beast: 31% → 16% at 50/50 split). "Tank the hit, get the attack in" is the dominant strategy — the race policy was already playing correctly. Machinery kept for experiments; race-only stays the default. |

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

~~Card-swap A/B UI~~ — DONE (deck editor + paired-z verdict, June 2026).
~~Adaptive K~~ — DONE (Patience knob, June 2026).
~~Multi-candidate compare~~ — DONE ("which of these reward cards?" ranking, June 2026).
~~Phase 3 / boss fights~~ — DONE (encounter mode, win-rate + outcome score, June 2026).

~~Intent-aware policy~~ — BUILT AND FALSIFIED as a default (June 2026, see
"Things we tested and ruled out"). The pieces (`ThresholdPolicy`, `IntentReader`,
`Portfolio`, `policy-bench`) remain for experiments.

1. **Per-turn play enumeration**: exhaustively order the hand within a turn
   (small space at 5-7 cards / 3 energy) and score leaves by outcome — replaces
   the policy question at the turn level. Validate with `policy-bench`.
2. **Smarter base policy generally**: "play Powers turn 1", "Vulnerable before
   attacking". Could shrink ε-needed and tighten CI. Validate with `policy-bench`.
3. **Subprocess parallelism**: ~6x throughput. Don't bother until #1-2 feel slow.

## UI notes (frontend)

- The sidebar is a numbered question-builder (1 Opponent → 2 Deck edits →
  3 Candidates) with read-only run info/relics and sim settings collapsed
  into `<details>` folds. The run footer is pinned (aside is a flex column;
  only `.sidebar-scroll` scrolls) and always shows a plain-English summary
  of what Run will do — the question is assembled from three places, so it
  gets restated in one.
- Sim settings hide behind an Effort preset (Quick 60/12/6, Standard
  200/30/12, Thorough 500/50/20 for seeds/K/patience). Editing the raw
  fields flips the preset to "custom"; `detectPreset()` keeps the select in
  sync with whatever the fields actually say.
- WebSocket auto-reconnects on disconnect (1-second backoff).
- Chart redraws are coalesced into a single requestAnimationFrame tick
  (`chartsDirty` flag) — at most one redraw per frame regardless of event rate.
  Final update happens on `done`/`abDone`.
- Deck-editor state (removals map + additions list) is cleared whenever the
  deck reloads from disk — stale edits against a changed save make no sense.
- The "freshest save wins" rule means if the user has both modded and unmodded
  profiles, whichever was last touched is what we read. Could add a profile
  picker later, but the typical user only plays one mode at a time.
- Card IDs in the save file (e.g. `CARD.STRIKE_IRONCLAD`) match `card.Id`
  from the game DLL exactly. If we ever see an "unknown card id" error,
  either the game added a new card or our `ModelDb.Init` didn't cover it.
