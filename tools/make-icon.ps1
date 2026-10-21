# Generates src\App\Assets\AndroidTVDesktop.ico (16..256 px, PNG-compressed entries) with System.Drawing.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src\App\Assets'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

function New-IconPng([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([Drawing.Color]::Transparent)
    $r = [single]($s * 0.22)
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $w = [single]($s - 1)
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($w - $r, 0, $r, $r, 270, 90)
    $path.AddArc($w - $r, $w - $r, $r, $r, 0, 90); $path.AddArc(0, $w - $r, $r, $r, 90, 90); $path.CloseFigure()
    $bg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s),
        [Drawing.Color]::FromArgb(255, 38, 110, 245), [Drawing.Color]::FromArgb(255, 116, 63, 230))
    $g.FillPath($bg, $path)
    # TV body
    $pen = New-Object Drawing.Pen ([Drawing.Color]::White), ([single][Math]::Max(1.2, $s * 0.07))
    $pen.LineJoin = 'Round'
    $tx = [single]($s * 0.18); $ty = [single]($s * 0.24); $tw = [single]($s * 0.64); $th = [single]($s * 0.42)
    $g.DrawRectangle($pen, $tx, $ty, $tw, $th)
    # stand
    $g.DrawLine($pen, [single]($s * 0.38), [single]($s * 0.80), [single]($s * 0.62), [single]($s * 0.80))
    $g.DrawLine($pen, [single]($s * 0.50), [single]($ty + $th), [single]($s * 0.50), [single]($s * 0.80))
    # play triangle
    $cx = $s * 0.5; $cy = $ty + $th / 2; $t = $s * 0.11
    $pts = [Drawing.PointF[]]@((New-Object Drawing.PointF ([single]($cx - $t * 0.7)), ([single]($cy - $t))),
                               (New-Object Drawing.PointF ([single]($cx - $t * 0.7)), ([single]($cy + $t))),
                               (New-Object Drawing.PointF ([single]($cx + $t)), ([single]$cy)))
    $g.FillPolygon([Drawing.Brushes]::White, $pts)
    $g.Dispose()
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($s in $sizes) { , (New-IconPng $s) }
$ico = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$len); $bw.Write([uint32]$offset); $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
[IO.File]::WriteAllBytes((Join-Path $outDir 'AndroidTVDesktop.ico'), $ico.ToArray())
Write-Output "Icon written: $(Join-Path $outDir 'AndroidTVDesktop.ico') ($($ico.Length) bytes)"
