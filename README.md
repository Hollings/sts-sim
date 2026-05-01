# StS2Sim — Headless Damage-Per-Turn Simulator

Runs the actual `sts2.dll` game logic in a console process to benchmark damage output of arbitrary card decks. No Godot scene tree, no rendering, no real Godot runtime.

## Build & run

```bash
cd StS2Sim
dotnet run -p:STS2GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```

## Sample output

```
Deck: All Strikes (10 Strike)
  Damage/turn: mean=18.0  p50=18.0  p95=18.0
  Sample trial: Turn 1: 18 via [STRIKE, STRIKE, STRIKE]   (3 energy, 3 strikes @ 6 dmg)

Deck: Strike+Bash heavy (5 Strike, 5 Bash)
  Damage/turn: mean=14.7   <- worse! Bash is 4 dmg/energy vs Strike's 6.
```

## How the bootstrap works

The game DLL was built for a Godot host. We trick it into running outside one.

1. **Set `TestMode.IsOn = true`** — flips `NonInteractiveMode.IsActive`, which short-circuits every `Cmd.Wait` / `Cmd.CustomScaledWait` in the codebase. No SceneTree timers, no awaits.
2. **Harmony-patch a few Godot.* entry points** that crash without the native runtime:
   - `Logger.GetIsRunningFromGodotEditor` → return false.
   - `ConsoleLogPrinter.Print` → `Console.WriteLine` instead of `GD.Print`.
   - `Godot.Time.GetTicksMsec` → return 0.
   - `CombatState.IterateHookListeners` → empty (Phase 1: no powers/relics).
   - `Creature.ToString` → bypass localization.
3. **Boot ModelDb manually**: `ModelDb.Init()` + `ModelIdSerializationCache.Init()` + `ModelDb.InitIds()`. Registers all 1611 game models (cards, monsters, relics, etc.) without touching Godot's resource pipeline.
4. **Set up combat state by hand**: `Player.CreateForNewRun<Ironclad>()`, then `new CombatState()` with `NullRunState`. Reflection sets `CombatManager.Instance._state` and flips `IsInProgress` to `true`.
5. **Use real game card playback**: `CardModel.OnPlayWrapper(...)` runs `StrikeIronclad.OnPlay → DamageCmd.Attack(6).Execute → CreatureCmd.Damage → Creature.LoseHpInternal`. Same code paths the live game uses.

## Known limits (Phase 1 only — DPS analysis)

- **No hooks fire**: no powers, relics, enchantments, afflictions affect damage. We `IterateHookListeners` -> empty. Burning Blood, Strength, etc. do nothing.
- **Card costs use `EnergyCost.GetResolved()`** — temporary cost reductions/increases mid-combat won't be applied.
- **No turn-end / turn-start triggers** — we just dump hand to discard between turns.
- **No multi-target / random-target attacks** verified (single-target only via BigDummy).

These can all be enabled in Phase 2 by progressively re-enabling parts of `CombatManager.SetUpCombat` and `IterateHookListeners`.

## Project layout

- `Program.cs` — entry point; installs assembly resolver and Godot shims.
- `GodotShims.cs` — Harmony patches that let `sts2.dll` think it's in a Godot host.
- `Harness.cs` — bootstrap + helpers for spinning up a single isolated combat.
- `DamagePerTurnSim.cs` — the actual benchmark (draw 5, play attacks, end turn, repeat).
- `Sim.cs` — defines decks under test and prints stats.
