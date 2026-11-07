# Builds the Windows game with the example world:
#
#   release/LivingNPC-Game-<version>-win-x64/
#     LivingNPC.exe (+ LivingNPC_Data, UnityPlayer.dll, ...)
#     World/base/...          the world the game opens (default: worlds/aetherhall)
#     README.txt, LICENSE, CREDITS.md
#   release/LivingNPC-Game-<version>-win-x64.zip
#
# Usage (from the repository root, with Unity closed on this project):
#   powershell -ExecutionPolicy Bypass -File tools\build-game.ps1 [-Version 0.1.1-alpha] [-World aetherhall] [-Unity <Unity.exe>]

param(
    [string]$Version = "0.1.1-alpha",
    [string]$World = "aetherhall",
    [string]$Unity = "C:\Program Files\Unity\Hub\Editor\6000.3.19f1\Editor\Unity.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "unity\LivingNPC"
$name = "LivingNPC-Game-$Version-win-x64"
$out = Join-Path $root "release\$name"
$zip = Join-Path $root "release\$name.zip"
$worldDir = Join-Path $root "worlds\$World"
$log = Join-Path $root "release\build-game.log"

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }

if (-not (Test-Path $Unity)) { throw "Unity not found: $Unity (pass -Unity <path to Unity.exe>)" }
if (-not (Test-Path (Join-Path $worldDir "base"))) { throw "World not found: $worldDir\base" }

Step "Clean $out"
New-Item -ItemType Directory -Force (Join-Path $root "release") | Out-Null
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }

Step "Unity build (this takes a few minutes; log: $log)"
$unityArgs = @("-batchmode", "-nographics", "-quit", "-projectPath", "`"$project`"",
               "-executeMethod", "LivingNPC.EditorTools.BuildGame.Windows", "-out", "`"$out`"", "-logFile", "`"$log`"")
$p = Start-Process -FilePath $Unity -ArgumentList $unityArgs -Wait -PassThru -NoNewWindow
if ($p.ExitCode -ne 0 -or -not (Test-Path (Join-Path $out "LivingNPC.exe"))) {
    Get-Content $log -Tail 40 | Write-Host
    throw "Unity build failed (exit code $($p.ExitCode)), see $log"
}
# Burst writes its debug symbols beside the game: they are not for players
Get-ChildItem $out -Directory -Filter "*_DoNotShip" | Remove-Item -Recurse -Force

Step "World: $World"
Copy-Item (Join-Path $worldDir "base") (Join-Path $out "World\base") -Recurse
if (Test-Path (Join-Path $worldDir "CREDITS.md")) { Copy-Item (Join-Path $worldDir "CREDITS.md") (Join-Path $out "CREDITS.md") }
Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE")

$readme = @"
LivingNPC $Version (Windows x64) - ALPHA
==========================================

Double-click LivingNPC.exe to play the example world, $World.

  Click the buttons on the picture to move from place to place.
  Click a character (or the "Present" list) to talk.
  Space / Enter      continue a dialogue
  Esc                game menu: save, load, settings, quit
  F6 / F9            quick save / quick load
  F5                 reload the world files (keeps the game in progress)

The world is in the World folder: plain JSON files and pictures. Change them with the LivingNPC editors (or any text
editor), press F5 in the game, and see the change. To play another world, replace World\base with it, or put several
worlds in a "Worlds" folder (Worlds\<world>\base) and pick one from the menu.

Saves and settings: %USERPROFILE%\AppData\LocalLow\LivingNPC\LivingNPC

This is an alpha: expect rough edges. The source code, the editors and the documentation are in the LivingNPC
repository. License: Mozilla Public License 2.0 (LICENSE). Example world credits: CREDITS.md.
"@
[IO.File]::WriteAllText((Join-Path $out "README.txt"), ($readme -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding $false))

Step "Zip"
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$size = "{0:N1} MB" -f ((Get-Item $zip).Length / 1MB)
Write-Host "Done: $out" -ForegroundColor Green
Write-Host "      $zip ($size)" -ForegroundColor Green
