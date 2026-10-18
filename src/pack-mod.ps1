# Packs a mod project into a .swyfmod (see MOD-FORMAT.md):
#   powershell -File ModSource/pack-mod.ps1 -ModDir ModSource/DevMenuUnlock
# Builds the project (Release), then zips mod.json + files/<dll> (+ .pdb, + anything in <ModDir>/files)
# into ModSource/dist/mods/<id>-<version>.swyfmod.
param([Parameter(Mandatory)][string]$ModDir, [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$ModDir = (Resolve-Path $ModDir).Path
$manifestPath = Join-Path $ModDir 'mod.json'
$manifest = Get-Content $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.id -notmatch '^[A-Za-z0-9_-]+$') { throw "mod.json: invalid id '$($manifest.id)'" }

$project = Get-ChildItem $ModDir -Filter *.csproj | Select-Object -First 1
if (-not $NoBuild -and $project) {
    dotnet build $project.FullName -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "build failed" }
}
$dll = Get-ChildItem (Join-Path $ModDir 'bin\Release') -Filter $manifest.dll -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $dll) { throw "built $($manifest.dll) not found under $ModDir\bin\Release" }

$stage = Join-Path ([IO.Path]::GetTempPath()) ("swyfmod-" + [Guid]::NewGuid())
New-Item -ItemType Directory "$stage\files" -Force | Out-Null
Copy-Item $manifestPath "$stage\mod.json"
Copy-Item $dll.FullName "$stage\files\"
$pdb = [IO.Path]::ChangeExtension($dll.FullName, '.pdb')
if (Test-Path $pdb) { Copy-Item $pdb "$stage\files\" }
$extra = Join-Path $ModDir 'files'
if (Test-Path $extra) { Copy-Item "$extra\*" "$stage\files\" -Recurse }

$distDir = Join-Path $PSScriptRoot 'dist\mods'
New-Item -ItemType Directory $distDir -Force | Out-Null
$out = Join-Path $distDir "$($manifest.id)-$($manifest.version).swyfmod"
if (Test-Path $out) { Remove-Item $out }
# Write entries by hand: Windows PowerShell's CreateFromDirectory stores "\" in paths, other tools expect "/".
$zip = [IO.Compression.ZipFile]::Open($out, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $name = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }
Remove-Item $stage -Recurse -Force
"Packed $out ($([int]((Get-Item $out).Length / 1KB)) KB)"
