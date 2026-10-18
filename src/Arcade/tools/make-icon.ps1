# Draws arcade-icon.png (the desktop icon of the Arkáda app): a purple tile with a white gamepad.
#   powershell -File ModSource/Arcade/tools/make-icon.ps1
Add-Type -AssemblyName System.Drawing
$size = 128
$bmp = New-Object Drawing.Bitmap $size, $size
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([Drawing.Color]::Transparent)

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, 2 * $r, 2 * $r, 180, 90)
    $p.AddArc($x + $w - 2 * $r, $y, 2 * $r, 2 * $r, 270, 90)
    $p.AddArc($x + $w - 2 * $r, $y + $h - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $p.AddArc($x, $y + $h - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $p.CloseFigure()
    $p
}

$tile = RoundRect 4 4 120 120 26
$grad = New-Object Drawing.Drawing2D.LinearGradientBrush (New-Object Drawing.Point 0, 0), (New-Object Drawing.Point 128, 128), ([Drawing.Color]::FromArgb(255, 123, 47, 247)), ([Drawing.Color]::FromArgb(255, 241, 7, 163))
$g.FillPath($grad, $tile)

# Gamepad body: two round grips joined by a bar.
$white = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 255, 255, 255))
$g.FillPath($white, (RoundRect 20 42 88 40 20))
$g.FillEllipse($white, 16, 50, 40, 46)
$g.FillEllipse($white, 72, 50, 40, 46)

# D-pad and buttons cut out in the tile colour.
$ink = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255, 150, 35, 215))
$g.FillRectangle($ink, 30, 59, 22, 8)
$g.FillRectangle($ink, 37, 52, 8, 22)
$g.FillEllipse($ink, 78, 52, 10, 10)
$g.FillEllipse($ink, 89, 62, 10, 10)
$g.FillEllipse($ink, 78, 70, 8, 8)

$out = Join-Path (Split-Path $PSScriptRoot -Parent) 'arcade-icon.png'
$bmp.Save($out, [Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"Saved $out"
