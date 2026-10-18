# Builds payload.zip for the hub: only the base that every player needs, taken from the files that
# currently work in the game folder:
#   base/  -> copied into the game root (doorstop, BepInEx core, unstripped BCL, StrippedFix patcher,
#             SWYF Mod Manager plugin)
# Mods themselves are distributed separately as .swyfmod packages (see ModSource/MOD-FORMAT.md).
param([Parameter(Mandatory)][string]$GameDir, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$GameDir = (Resolve-Path $GameDir).Path
$stage = Join-Path ([IO.Path]::GetTempPath()) ("swyf-payload-" + [Guid]::NewGuid())
New-Item -ItemType Directory "$stage\base\BepInEx\patchers\StrippedFix", "$stage\base\BepInEx\plugins\SwyfModManager" -Force | Out-Null

foreach ($f in 'winhttp.dll', 'doorstop_config.ini', '.doorstop_version') { Copy-Item (Join-Path $GameDir $f) "$stage\base\" }
Copy-Item (Join-Path $GameDir 'BepInEx\core') "$stage\base\BepInEx\core" -Recurse
Copy-Item (Join-Path $GameDir 'BepInEx\unstripped_corlib') "$stage\base\BepInEx\unstripped_corlib" -Recurse
Copy-Item (Join-Path $PSScriptRoot '..\StrippedFix\bin\Release\StrippedFix.dll') "$stage\base\BepInEx\patchers\StrippedFix\"
Copy-Item (Join-Path $PSScriptRoot '..\ModManager\bin\Release\SwyfModManager.dll') "$stage\base\BepInEx\plugins\SwyfModManager\"

New-Item -ItemType Directory (Split-Path $Out -Parent) -Force | Out-Null
if (Test-Path $Out) { Remove-Item $Out }
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $Out, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $stage -Recurse -Force
"payload.zip: $([int]((Get-Item $Out).Length / 1KB)) KB (base + Mod Manager)"
