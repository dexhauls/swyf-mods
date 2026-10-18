# Converts <in>/<Dance>/{intro,loop}.wav (made by icon_pack.py) to <out>/<Dance>.ogg and <Dance>.intro.ogg.
#   powershell -File convert-music.ps1 -In private\icon\audio -Out private\music-icon
param([Parameter(Mandatory)][string]$In, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Stop'
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source
if (-not $ffmpeg) { $ffmpeg = Get-ChildItem "$env:LOCALAPPDATA\Microsoft\WinGet\Packages" -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName }
New-Item -ItemType Directory $Out -Force | Out-Null
foreach ($dir in Get-ChildItem $In -Directory) {
    foreach ($part in 'intro', 'loop') {
        $src = Join-Path $dir.FullName "$part.wav"
        if (-not (Test-Path $src)) { continue }
        $name = if ($part -eq 'intro') { "$($dir.Name).intro.ogg" } else { "$($dir.Name).ogg" }
        & $ffmpeg -y -loglevel error -i $src -c:a libvorbis -q:a 4 (Join-Path $Out $name)
    }
}
$size = (Get-ChildItem $Out | Measure-Object Length -Sum).Sum / 1MB
"Converted $((Get-ChildItem $Out).Count) files, {0:n1} MB" -f $size
