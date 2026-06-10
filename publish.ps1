# Package StS2Sim as a self-contained zip: no .NET install, no git, no build
# tools needed on the target machine — just Slay the Spire 2 via Steam.
#
#   .\publish.ps1                      # uses the default Steam install path
#   .\publish.ps1 -GameDir "D:\SteamLibrary\steamapps\common\Slay the Spire 2"
#
# Output: dist\StS2Sim-win64.zip — unzip anywhere, double-click StS2Sim.exe.
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2",
    [string]$OutDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = "Stop"
$stage = Join-Path $OutDir "StS2Sim"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# Self-contained: bundles the .NET runtime (~70 MB unzipped). The game's own
# DLLs (sts2, GodotSharp, 0Harmony) are intentionally NOT bundled — they're
# referenced with Private=false and resolved at runtime from the player's
# Steam install, which also means no MegaCrit code ships in the zip.
dotnet publish $PSScriptRoot -c Release -r win-x64 --self-contained true `
    -p:STS2GameDir=$GameDir -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

@"
StS2Sim — Slay the Spire 2 Deck Analyzer
=========================================

WHAT YOU NEED
  - Windows, 64-bit
  - Slay the Spire 2 installed via Steam
  - A run in progress (the sim reads the game's autosave)
  That's it: no .NET install, no mods, the game doesn't even need to be running.

HOW TO RUN
  1. Unzip this folder anywhere.
  2. Double-click StS2Sim.exe.
  3. Your browser opens http://localhost:52324 with your current deck loaded.

WHAT IT DOES
  - Pick an opponent (any boss/elite/normal encounter, or a training dummy)
  - Click deck cards to test removals, add cards to test pickups (A/B test)
  - Add 2-4 candidate cards to answer "which of these rewards should I take?"
  - Verdicts are statistical: win rates and damage with confidence intervals,
    simulated with the real game logic (it runs the game's own code headless).

TROUBLESHOOTING
  - "Couldn't find Slay the Spire 2": the game isn't in a standard Steam
    location. Set the STS2_GAME_DIR environment variable to the game's
    data_sts2_windows_x86_64 folder and run again.
  - "could not bind port 52324": something else is using the port. Set the
    STS2SIM_PORT environment variable to another port (e.g. 52399).
  - Windows Firewall may ask for permission on first run — it's a local-only
    web server (it binds 127.0.0.1; nothing is exposed to the network).
  - "No save file found": start (or load) a run in the game first, then click
    the reload button in the sidebar.

NOTE
  After a game patch, numbers update automatically — the sim runs the game's
  own DLL, it doesn't re-implement cards. If a patch renames internals the
  sim may need an update instead.
"@ | Set-Content -Path (Join-Path $stage "README.txt") -Encoding UTF8

$zip = Join-Path $OutDir "StS2Sim-win64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "`nPackaged: $zip ($sizeMb MB)"
Write-Host "Ship that zip. Unzip + double-click StS2Sim\StS2Sim.exe on the target machine."
