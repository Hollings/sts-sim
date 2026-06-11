# StS2Sim — Headless Deck Simulator

Runs the actual `sts2.dll` game logic in a console process to benchmark damage output of arbitrary card decks — and to answer "should I add card X?" with a statistically grounded A/B test. No Godot scene tree, no rendering, no real Godot runtime.

## Build & run

```bash
cd StS2Sim
dotnet run -c Release -p:STS2GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

## Package for someone else (no git, no .NET, no build tools)

```powershell
cd StS2Sim
.\publish.ps1     # → dist\StS2Sim-win64.zip (~35 MB)
```

The zip is self-contained: unzip anywhere, double-click `StS2Sim.exe`, browser
opens. The target machine only needs Windows and Slay the Spire 2 installed via
Steam — the game's location is auto-discovered through the Steam registry key
and `libraryfolders.vdf` (any drive), overridable with `STS2_GAME_DIR`. The
game's own DLLs are resolved from the player's install at runtime and are
never bundled, so the zip contains no MegaCrit code. A `README.txt` with run
and troubleshooting instructions is included in the zip.

Default mode starts the embedded web UI on `http://localhost:52324` and opens the browser. It reads your freshest `current_run.save`, shows the deck, and lets you:

- run a best-of-K damage sim with live charts (per-seed scatter, running average ± CI, histogram),
- click cards to mark removals and pick cards to add, then run an **A/B comparison** — baseline vs edited deck on identical shuffle seeds with a paired z-test verdict ("ADD IT / DON'T / INCONCLUSIVE"),
- answer the card-reward question directly: add 2–4 **compare candidates** and get a ranked table — each candidate tested as +1 card on identical shuffles, paired lift vs skipping, plus a winner-vs-runner-up significance test ("TAKE BLUDGEON — beats your current deck and clearly beats the other options"),
- or pick a **real opponent**: any boss, elite, or normal encounter in the game. The sim then runs the whole fight — genuine monster AI through `MonsterMoveStateMachine`, real damage to the player, block, minion spawns — and ranks the deck by outcome (win rate + HP kept on win / boss HP left on loss). A/B and compare both work against an opponent, so "which of these three cards best helps me beat Vantom?" is one click.

Other modes:

```bash
dotnet run -c Release -- smoke            # 15 fast Ironclad assertion tests
dotnet run -c Release -- silent-tests     # 174-test Silent card battery (exit 2 on harness crashes)
dotnet run -c Release -- encounter-sweep  # one short fight vs all 80 encounters (exit 2 on crashes)
dotnet run -c Release -- character-sweep  # starter-deck trial per character (exit 2 on crashes)
dotnet run -c Release -- char-tests       # 45-test Regent/Necrobinder/Defect mechanics battery (exit 2 on crashes)
dotnet run -c Release -- card-sweep       # play every card once, base + upgraded (exit 2 on crash/hang)
dotnet run -c Release -- policy-bench     # play-policy uplift benchmark
dotnet run -c Release -- experiment       # legacy console K-curve + unpaired A/B
```

All five characters work (Ironclad, Silent, Regent, Necrobinder, Defect) — the
sim reads whoever your current run is playing. Every character now has its own
assertion battery: Ironclad (smoke), Silent (174 tests, the deepest), and
`char-tests` covering the fragile mechanics of the other three — Defect orbs,
Necrobinder's Osty/Souls, Regent's stars and Forge.

## How it works (short version)

The game DLL was built for a Godot host; `Harness.Bootstrap()` tricks it into running outside one: `TestMode` short-circuits every animation wait, a handful of Harmony shims stub out the native-interop entry points (logger, `Time.GetTicksMsec`, localization, shuffle pacing), and `ModelDb` is initialized directly so all 1611 game models register without the resource pipeline. Combat state is then assembled by hand per trial: real `Player`, real `CombatState`, a 9999-HP BigDummy target, and the real hook pipeline (relics + powers fire through `IterateHookListeners`).

Card plays go through the game's own code: `card.SpendResources()` (correct X-cost capture and energy debits) followed by `card.OnPlayWrapper(...)` — so Strike does what Strike does in the live game, after every patch, with no reimplementation.

See [CLAUDE.md](CLAUDE.md) for the full bootstrap walkthrough, project layout, the best-of-K + paired-test algorithm rationale, and known footguns.

## Accuracy status

- Phase 1 (card piles, draw, energy, block, exhaust, multi-hit) — done, smoke-tested.
- Phase 2 (powers, relics, hooks, turn-cycle events) — done, verified by the Silent battery (166/174 pass, 8 skips for unimplemented mechanics like multi-target) and the character battery (44/44: orbs incl. end-of-turn passives, Osty, stars/Forge, all three starter relics).
- Phase 3 (real enemy turns / survivability) — done; all 80 encounters across all four acts (including Underdocks) run crash-free (`encounter-sweep`). Caveat: the play policy doesn't read enemy intents yet, so it only blocks when ε-exploration stumbles into it — win rates are a fair *comparator* between decks but a *lower bound* on absolute winnability.
