# Renders the MultiSpeaker logo at every icon size and packs them into MultiSpeaker.ico (+ logo.png).
# Run from anywhere: powershell -ExecutionPolicy Bypass -File Assets\make-logo.ps1
Add-Type -AssemblyName System.Drawing

$outDir = $PSScriptRoot

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# Drawn on a 256-unit canvas and scaled, so each size is rendered sharply rather than downsampled.
function New-Logo([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    $blue = [System.Drawing.Color]::FromArgb(37, 99, 235)
    $violet = [System.Drawing.Color]::FromArgb(124, 58, 237)
    $indigo = [System.Drawing.Color]::FromArgb(79, 70, 229)
    $white = [System.Drawing.Color]::White

    # Tile background
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new(256, 256)), $blue, $violet
    $g.FillPath($bg, (New-RoundedRect 8 8 240 240 56))

    # Speaker cabinet with tweeter and woofer
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), (New-RoundedRect 94 58 68 140 18))
    $accent = New-Object System.Drawing.SolidBrush $indigo
    $g.FillEllipse($accent, 116, 82, 24, 24)
    $g.FillEllipse($accent, 104, 128, 48, 48)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush $white), 121, 145, 14, 14)

    # Sound waves going out to both sides (= many speakers); tiny sizes get one wave per side
    $penWidth = [Math]::Max(14, 1.4 / $s)
    $pen = New-Object System.Drawing.Pen $white, $penWidth
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $radii = if ($size -le 24) { @(80) } else { @(64, 96) }
    foreach ($r in $radii) {
        $g.DrawArc($pen, 128 - $r, 128 - $r, $r * 2, $r * 2, -38, 76)
        $g.DrawArc($pen, 128 - $r, 128 - $r, $r * 2, $r * 2, 142, 76)
    }

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = New-Logo $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $outDir 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one 16-byte directory entry per image, then the PNG payloads.
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $outDir 'MultiSpeaker.ico'), $out.ToArray())
"Wrote MultiSpeaker.ico and logo.png to $outDir"
