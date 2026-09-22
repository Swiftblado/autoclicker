# Draws the app icon (blue tile with a cursor and click ripples) and writes every format the
# three platforms need: app.ico (Windows), a macOS .iconset folder, and PNGs (Linux + in-app).
param([string]$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')))

Add-Type -AssemblyName System.Drawing

$iconsDir = Join-Path $Root 'packaging/icons'
$iconsetDir = Join-Path $iconsDir 'AutoClicker.iconset'
$assetsDir = Join-Path $Root 'src/AutoClicker/Assets'
New-Item -ItemType Directory -Force $iconsDir, $iconsetDir, $assetsDir | Out-Null

function New-IconPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    # Rounded tile
    $tile = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 60; $x = 8; $y = 8; $w = 240; $h = 240
    $tile.AddArc($x, $y, $r, $r, 180, 90)
    $tile.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $tile.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $tile.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $tile.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush `
        (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point 256, 256), `
        ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillPath($fill, $tile)

    # Click ripples around the cursor tip
    $tipX = 104; $tipY = 80
    $ripple = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 255, 255, 255)), 14
    $ripple.StartCap = 'Round'; $ripple.EndCap = 'Round'
    $g.DrawArc($ripple, $tipX - 34, $tipY - 34, 68, 68, 195, 60)
    $g.DrawArc($ripple, $tipX - 62, $tipY - 62, 124, 124, 195, 60)

    # Cursor arrow
    $pts = @(@(0, 0), @(0, 134), @(34, 104), @(58, 156), @(82, 145), @(58, 94), @(100, 92)) | ForEach-Object {
        New-Object System.Drawing.PointF ($tipX + $_[0]), ($tipY + $_[1])
    }
    $outline = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(15, 23, 42)), 12
    $outline.LineJoin = 'Round'
    $g.DrawPolygon($outline, [System.Drawing.PointF[]]$pts)
    $g.FillPolygon([System.Drawing.Brushes]::White, [System.Drawing.PointF[]]$pts)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

# PNGs, cached per size
$png = @{}
foreach ($size in 16, 32, 64, 128, 256, 512, 1024) { $png[$size] = New-IconPng $size }

# Windows .ico
$icoSizes = 256, 64, 48, 32, 16
$icoImages = @($icoSizes | ForEach-Object { , (New-IconPng $_) })
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$icoSizes.Count)
$offset = 6 + 16 * $icoSizes.Count
for ($i = 0; $i -lt $icoSizes.Count; $i++) {
    $dim = if ($icoSizes[$i] -ge 256) { 0 } else { $icoSizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$icoImages[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $icoImages[$i].Length
}
foreach ($img in $icoImages) { $bw.Write([byte[]]$img) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $iconsDir 'app.ico'), $out.ToArray())

# macOS iconset (iconutil turns this into AutoClicker.icns on a Mac)
$iconset = @{
    'icon_16x16.png' = 16; 'icon_16x16@2x.png' = 32
    'icon_32x32.png' = 32; 'icon_32x32@2x.png' = 64
    'icon_128x128.png' = 128; 'icon_128x128@2x.png' = 256
    'icon_256x256.png' = 256; 'icon_256x256@2x.png' = 512
    'icon_512x512.png' = 512; 'icon_512x512@2x.png' = 1024
}
foreach ($name in $iconset.Keys) {
    [System.IO.File]::WriteAllBytes((Join-Path $iconsetDir $name), $png[$iconset[$name]])
}

# Linux desktop icon + the icon shown in the app's own title bar
[System.IO.File]::WriteAllBytes((Join-Path $iconsDir 'icon-256.png'), $png[256])
[System.IO.File]::WriteAllBytes((Join-Path $iconsDir 'icon-512.png'), $png[512])
[System.IO.File]::WriteAllBytes((Join-Path $assetsDir 'icon.png'), $png[256])

Write-Host "Icons written to $iconsDir and $assetsDir"
