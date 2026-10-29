# Generates src\ScreenDeck\ScreenDeck.ico (16–256 px, PNG-compressed frames) in the app's palette.
# Run once after changing the design; the .ico is committed and used by the app and the installer.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\src\ScreenDeck\ScreenDeck.ico'
$sizes = 16, 24, 32, 48, 64, 128, 256

function Color($hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

# Draws the design on a 256-unit canvas scaled to $size.
function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.ScaleTransform($size / 256, $size / 256)

    $accent = Color '#4C8DFF'
    $dim = Color '#8B93A3'

    # Background tile
    $bg = RoundRect 8 8 240 240 48
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, 8), (New-Object System.Drawing.PointF 0, 248), (Color '#2A303B'), (Color '#16181D')
    $g.FillPath($grad, $bg)
    $g.DrawPath((New-Object System.Drawing.Pen (Color '#343B48'), 4), $bg)

    # Left monitor: source (outlined)
    $g.DrawPath((New-Object System.Drawing.Pen $dim, 12), (RoundRect 30 58 86 64 10))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $dim), 65, 126, 16, 14)
    $g.FillPath((New-Object System.Drawing.SolidBrush $dim), (RoundRect 50 138 46 10 5))

    # Right monitor: target (filled accent with a window on it)
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), (RoundRect 134 52 96 76 12))
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), (RoundRect 150 68 64 44 6))
    $g.FillRectangle((New-Object System.Drawing.SolidBrush $accent), 174, 128, 16, 14)
    $g.FillPath((New-Object System.Drawing.SolidBrush $accent), (RoundRect 158 140 48 10 5))

    # Arrow: source → target
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 14
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $g.DrawBezier($pen, 70, 178, 100, 214, 150, 214, 176, 186)
    $head = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF 196, 162),
        (New-Object System.Drawing.PointF 192, 204),
        (New-Object System.Drawing.PointF 156, 184))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)), $head)

    $g.Dispose()
    $bmp
}

# ICO container with PNG frames.
$frames = foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256))   # 256 is stored as 0
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
$w.Dispose()
Get-Item $out
