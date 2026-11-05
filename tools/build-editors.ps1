# Builds the portable Windows package of the two editors:
#
#   release/LivingNPC-Editors-<version>-win-x64/
#     LivingNPC Studio.exe        starts studio/ with the bundled Node.js and opens the browser
#     LivingNPC Map Editor.exe    the same for map-editor/
#     runtime/node.exe            Node.js (MIT, see runtime/LICENSE)
#     studio/  map-editor/        the editors (map-editor with its built page and the production node_modules)
#   release/LivingNPC-Editors-<version>-win-x64.zip
#
# Usage (from the repository root, Windows PowerShell 5.1 or PowerShell 7):
#   powershell -ExecutionPolicy Bypass -File tools\build-editors.ps1 [-Version 0.1.0-alpha]
#
# Needs: Node.js 20+ and npm on PATH (the node.exe found there is the one bundled), internet for npm and the Node.js
# license, and the C# compiler of .NET Framework 4 (present on every Windows 10/11).

param([string]$Version = "0.1.0-alpha")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$name = "LivingNPC-Editors-$Version-win-x64"
$out = Join-Path $root "release\$name"
$zip = Join-Path $root "release\$name.zip"

function Step($text) { Write-Host "== $text" -ForegroundColor Cyan }
function Run($exe, [string[]]$arguments, $dir) {
    Push-Location $dir
    try {
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed (exit code $LASTEXITCODE)" }
    } finally { Pop-Location }
}
function CopyTree($from, $to) {
    New-Item -ItemType Directory -Force (Split-Path -Parent $to) | Out-Null
    Copy-Item $from $to -Recurse -Force
}

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }
$node = (Get-Command node -ErrorAction Stop).Source
$nodeVersion = (& $node --version).Trim()

Step "Clean $out"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $out | Out-Null

Step "Build the map editor page"
$mapSrc = Join-Path $root "map-editor"
if (-not (Test-Path (Join-Path $mapSrc "node_modules"))) { Run "npm.cmd" @("ci", "--no-audit", "--no-fund") $mapSrc }
Run "npm.cmd" @("run", "build") $mapSrc

Step "Node.js runtime $nodeVersion"
New-Item -ItemType Directory -Force (Join-Path $out "runtime") | Out-Null
Copy-Item $node (Join-Path $out "runtime\node.exe")
Invoke-WebRequest -UseBasicParsing -Uri "https://raw.githubusercontent.com/nodejs/node/$nodeVersion/LICENSE" -OutFile (Join-Path $out "runtime\LICENSE")

Step "LivingNPC Studio"
$studioSrc = Join-Path $root "studio"
$studioOut = Join-Path $out "studio"
foreach ($item in @("server.mjs", "package.json", "README.md", "lib", "public")) { CopyTree (Join-Path $studioSrc $item) (Join-Path $studioOut $item) }

Step "LivingNPC Map Editor"
$mapOut = Join-Path $out "map-editor"
foreach ($item in @("server.mjs", "README.md", "dist")) { CopyTree (Join-Path $mapSrc $item) (Join-Path $mapOut $item) }
# the server only needs sharp at run time (the page is already built in dist/)
$sharpVersion = (& $node -p "require('$($mapSrc -replace '\\','/')/node_modules/sharp/package.json').version").Trim()
$package = [ordered]@{ name = "livingnpc-map-editor"; private = $true; version = $Version; type = "module"; dependencies = [ordered]@{ sharp = $sharpVersion } }
[IO.File]::WriteAllText((Join-Path $mapOut "package.json"), ($package | ConvertTo-Json) + "`n", (New-Object Text.UTF8Encoding $false))
Run "npm.cmd" @("install", "--omit=dev", "--no-audit", "--no-fund", "--no-package-lock") $mapOut

Step "Launchers"
$launcher = Join-Path $PSScriptRoot "launcher\Launcher.cs"
Run $csc @("/nologo", "/target:exe", "/optimize+", "/define:STUDIO", "/out:$(Join-Path $out 'LivingNPC Studio.exe')", $launcher) $out
Run $csc @("/nologo", "/target:exe", "/optimize+", "/define:MAPEDITOR", "/out:$(Join-Path $out 'LivingNPC Map Editor.exe')", $launcher) $out

Step "License and read-me"
Copy-Item (Join-Path $root "LICENSE") (Join-Path $out "LICENSE")
$readme = @"
LivingNPC editors $Version (Windows x64) - ALPHA
=================================================

  LivingNPC Studio.exe       quests and messages as code; characters, items, rules, title screen
                             http://127.0.0.1:5180
  LivingNPC Map Editor.exe   places, backgrounds and links
                             http://127.0.0.1:5173

Double-click an exe: a console window opens, the editor starts and your browser shows it.
Close the console window to stop the editor. Nothing needs to be installed (Node.js $nodeVersion is included in
runtime\).

Where the worlds are
  The editors work on a "worlds" folder: the first one found next to the exes or in the folders above them
  (so you can keep this folder inside the LivingNPC repository), otherwise a new "worlds" folder is created here.
  Set the LIVINGNPC_WORLDS environment variable to use another folder.

  A new world is a folder with one file: worlds\<your_world>\base\pack.json containing
  { "id": "base", "name": "Your world" }. Open it in the Map Editor with "Load World".

The game itself, the documentation (docs\STUDIO_COMMANDS.md, docs\JSON_REFERENCE.md) and the source code are in the
LivingNPC repository.

License: Mozilla Public License 2.0 (LICENSE). Node.js: MIT and others (runtime\LICENSE).
"@
[IO.File]::WriteAllText((Join-Path $out "README.txt"), ($readme -replace "`r?`n", "`r`n"), (New-Object Text.UTF8Encoding $false))

Step "Zip"
Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
$size = "{0:N1} MB" -f ((Get-Item $zip).Length / 1MB)
Write-Host "Done: $out" -ForegroundColor Green
Write-Host "      $zip ($size)" -ForegroundColor Green
