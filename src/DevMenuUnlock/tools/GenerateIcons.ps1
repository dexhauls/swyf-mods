# Renders every icon code point used in MenuLabels.cs (\uEXXX) from the Windows icon font into Icons.bin,
# which is embedded in the mod. The game can't load OS fonts (Unity stripped Font.CreateDynamicFontFromOSFont),
# so icons ship as alpha masks instead.
#
# Format: int32 count, int32 size, then per icon: uint16 code point + size*size alpha bytes (rows top-down).
# Run with Windows PowerShell 5.1:  powershell -File tools\GenerateIcons.ps1
param([int]$Size = 32)

Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$source = Get-Content (Join-Path $root 'MenuLabels.cs') -Raw -Encoding UTF8
# Icon glyphs live in the Private Use Area; they may appear as \uEXXX escapes or as literal characters.
$escaped = [regex]::Matches($source, '\\u(E[0-9A-Fa-f]{3})') | ForEach-Object { [Convert]::ToInt32($_.Groups[1].Value, 16) }
$literal = $source.ToCharArray() | ForEach-Object { [int]$_ } | Where-Object { $_ -ge 0xE000 -and $_ -le 0xF8FF }
$codes = @($escaped) + @($literal) | Where-Object { $_ } | Sort-Object -Unique

$fontName = 'Segoe Fluent Icons'
if (-not ((New-Object System.Drawing.Text.InstalledFontCollection).Families.Name -contains $fontName)) { $fontName = 'Segoe MDL2 Assets' }
$font = New-Object System.Drawing.Font($fontName, ($Size * 0.72), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = 'Center'; $format.LineAlignment = 'Center'

$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([int]$codes.Count); $w.Write([int]$Size)
foreach ($code in $codes) {
    $bmp = New-Object System.Drawing.Bitmap($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
    $g.DrawString([string][char]$code, $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 0, $Size, $Size)), $format)
    $g.Dispose()
    $w.Write([uint16]$code)
    for ($y = 0; $y -lt $Size; $y++) { for ($x = 0; $x -lt $Size; $x++) { $w.Write([byte]$bmp.GetPixel($x, $y).A) } }
    $bmp.Dispose()
}
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $root 'Icons.bin'), $out.ToArray())
"Wrote $($codes.Count) icons ($fontName, ${Size}px) to Icons.bin"
