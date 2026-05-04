# Silent card test battery — agent brief

You are one of six parallel agents writing tests for Silent's 88 cards in the StS2 headless sim. Your bucket is a slice of those cards; the other agents handle the rest. **Stay in your file** — don't touch other bucket files or shared infrastructure.

## Your job

For every card in your assigned bucket (listed in your file's class doc-comment), write a test that:

1. **Reads the card's actual implementation** at `C:\Users\jhol\slaythespiredata\sts2-decompiled\MegaCrit\sts2\Core\Models\Cards\<CardName>.cs` to derive the expected behavior. **Do not guess damage/block numbers from STS1 memory** — STS2 numbers differ.
2. **Asserts a specific delta** (HP, block, energy, hand size, presence of a power).
3. **Tests both the base card and the upgraded variant** (`+`). Two test methods per card.
4. **Returns a `TestHelpers.TestResult`** so the runner can categorize PASS / FAIL / CRASH / SKIPPED.

## Test patterns (use these — don't reinvent)

All four live in `TestHelpers.cs`. Match the pattern to the card:

| Pattern | When to use | Example |
|---|---|---|
| `SingleCardTest<TCard>` | Card does its thing in isolation: damage, block, applies a power | `Strike deals 6` |
| `PowerThenPlayTest<TPower, TCard>` | Card's value depends on a power being active | `Strike into Vulnerable does 9` |
| `SequenceTest` | Need to play multiple cards and check the final state | `Inflame then Strike does 8` |
| `PreloadHandTest<TCard>` | Effect depends on what's in hand (HandTrick, Reflex, CalculatedGamble) | `HandTrick scales with cards in hand` |

**If a card needs something none of these patterns supports** (random target, monster intent, shuffle a specific seed, choose-card-from-discard) — call `TestHelpers.Skip(name, reason)` with a clear reason. Don't write an unreliable test. Skips are first-class results.

## File structure

Your file already exists with the class scaffolding. Replace the `RunAll()` body with something like:

```csharp
public static async Task<IReadOnlyList<TestHelpers.TestResult>> RunAll()
{
    var results = new List<TestHelpers.TestResult>();
    results.Add(await Test_StrikeSilent());
    results.Add(await Test_StrikeSilentPlus());
    results.Add(await Test_Sneaky());
    results.Add(await Test_SneakyPlus());
    // ... one Add per test
    return results;
}

private static Task<TestHelpers.TestResult> Test_StrikeSilent()
    => TestHelpers.SingleCardTest<StrikeSilent>(
        name: "StrikeSilent deals 6",
        assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 6, "damage"));

private static Task<TestHelpers.TestResult> Test_StrikeSilentPlus()
    => TestHelpers.SingleCardTest<StrikeSilent>(
        name: "StrikeSilent+ deals 9",
        upgradeLevel: 1,
        assert: (h, before) => TestHelpers.Expect(before.dummyHp - h.Dummy.CurrentHp, 9, "damage"));
```

Methods can be sync (`Task.FromResult` via the helper) or `async` — both work because helpers return `Task<TestResult>`.

## Reading card source

Each `*.cs` card file has an `OnPlay` method that is the source of truth. Look for:
- `DamageCmd.Attack(<value>)` — base damage. The value is from `DynamicVars.Damage.BaseValue`.
- `BlockCmd.Add(<value>)` — block.
- `PowerCmd.Apply<XPower>(target, amount, ...)` — power application.
- `OnUpgrade()` — what changes when upgraded. Often `BaseValue.UpgradeValueBy(N)` or `EnergyCost.UpgradeBy(-1)`.

Example — `Neutralize.cs`:
```csharp
new DamageVar(3m, ValueProp.Move),  // base damage 3
new PowerVar<WeakPower>(1m)          // applies 1 Weak
// ...
protected override void OnUpgrade()
{
    base.DynamicVars.Damage.UpgradeValueBy(1m);  // +1 dmg → 4
    base.DynamicVars.Weak.UpgradeValueBy(1m);    // +1 Weak → 2
}
```

So `Test_Neutralize` expects 3 damage + 1 Weak; `Test_NeutralizePlus` expects 4 damage + 2 Weak.

## Power IDs (for `ExpectPower`)

Use a substring match because the game's `Id.Entry` strings are ALL_CAPS_UNDERSCORED:

- `"VULNERABLE"`, `"WEAK"`, `"FRAIL"`
- `"STRENGTH"`, `"DEXTERITY"`
- `"POISON"`
- `"ARTIFACT"`, `"INTANGIBLE"`

Example:
```csharp
assert: (h, before) =>
{
    if (before.dummyHp - h.Dummy.CurrentHp != 3) return "wrong damage";
    return TestHelpers.ExpectPower(h.Dummy, "WEAK", expectedAmount: 1);
}
```

## Naming convention

- Method name: `Test_<CardName>` and `Test_<CardName>Plus`
- Display name (the `name:` arg): `"<CardName> <human description>"` — keep it short, this is what shows in the test runner output.

## What counts as a PASS, FAIL, CRASH, or SKIPPED

- **PASS** = the assertion returned `null`. Damage/block/power matched expected.
- **FAIL** = your assertion returned a string. Don't guess; the runner shows your string verbatim.
- **CRASH** = the harness threw an exception running the card. **Do not try to fix this.** Just let it crash — the runner classifies it. The test author for the harness will triage.
- **SKIPPED** = you decided the card uses a mechanic the harness can't handle. Use `TestHelpers.Skip(name, reason)` and write a one-sentence reason.

## Don't do

- Don't add new helpers to `TestHelpers.cs` — that's shared infrastructure other agents are using simultaneously.
- Don't touch other bucket files.
- Don't try to fix harness crashes — log them, move on. The whole point is to find them.
- Don't fabricate damage numbers. If you can't tell what the card does from its source, mark it `Skip`.
- Don't add tests for cards that aren't in your bucket.
- Don't use Python scripts with emoji output.

## Verifying your work

When you're done, build:
```
cd C:\Users\jhol\slaythespiredata\StS2Sim
dotnet build -c Release -p:STS2GameDir="C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
```
Must end with `0 Error(s)`. Server may be running on port 52324 — if the build fails because `StS2Sim.exe` is locked, run `Stop-Process -Name "StS2Sim" -Force -ErrorAction SilentlyContinue` first.

You don't need to run the full battery yourself — the orchestrator will do that across all six buckets at the end. Just make sure your file compiles.

## Reporting back

When done, return a brief summary:
- Total tests written (target: 2× the number of cards in your bucket)
- Anything you `Skip`ped and why
- Anything that surprised you about the card mechanics (might be a bug in our harness)

That's it. Get going.
