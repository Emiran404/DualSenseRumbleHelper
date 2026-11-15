# Generates Resources\app.ico (PNG-compressed, 16/24/32/48/64/128/256 px) with System.Drawing.
param([string]$Out = (Join-Path $PSScriptRoot "..\DualSenseRumbleHelper\Resources\app.ico"))
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    # rounded dark tile
    $r = [math]::Max(2, $s * 0.22)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.RectangleF 0.5, 0.5, ($s - 1), ($s - 1)
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90)
    $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush ($rect), ([System.Drawing.Color]::FromArgb(37, 99, 235)), ([System.Drawing.Color]::FromArgb(17, 24, 39)), 60
    $g.FillPath($brush, $path)
    # vibration arcs (white) around a centre dot
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([math]::Max(1, $s * 0.075))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cx = $s / 2; $cy = $s / 2
    $dot = $s * 0.16
    $g.FillEllipse([System.Drawing.Brushes]::White, $cx - $dot / 2, $cy - $dot / 2, $dot, $dot)
    foreach ($k in 1, 2) {
        $rad = $s * (0.20 + 0.13 * $k)
        $g.DrawArc($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, 135, 90)   # left arc
        $g.DrawArc($pen, $cx - $rad, $cy - $rad, 2 * $rad, 2 * $rad, -45, 90)   # right arc
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += ,@{ Size = $s; Data = $ms.ToArray() }
    $g.Dispose(); $bmp.Dispose(); $pen.Dispose(); $brush.Dispose()
}
# ICO container
$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($p in $pngs) {
    $sz = if ($p.Size -ge 256) { 0 } else { $p.Size }
    $bw.Write([byte]$sz); $bw.Write([byte]$sz); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$p.Data.Length); $bw.Write([UInt32]$offset)
    $offset += $p.Data.Length
}
foreach ($p in $pngs) { $bw.Write($p.Data) }
$bw.Flush(); $fs.Close()
"icon written: $Out ($((Get-Item $Out).Length) bytes)"
