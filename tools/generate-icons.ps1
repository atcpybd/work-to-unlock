<#
.SYNOPSIS
    Regenerates the application and tray icons in assets/icons (Windows PowerShell 5.1 or PowerShell 7 on Windows).
.DESCRIPTION
    Draws a padlock glyph on a rounded tile for each state and packs PNG frames into multi-size .ico files.
#>
[CmdletBinding()]
param(
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\assets\icons'
}
Add-Type -AssemblyName System.Drawing

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-Frame([int]$size, [string]$top, [string]$bottom, [bool]$open) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(0.5, $size * 0.04)
    $tile = New-RoundedPath $pad $pad ($size - 2 * $pad) ($size - 2 * $pad) ($size * 0.22)
    $rect = New-Object System.Drawing.RectangleF 0, 0, $size, $size
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.ColorTranslator]::FromHtml($top)), ([System.Drawing.ColorTranslator]::FromHtml($bottom)), 90
    $g.FillPath($brush, $tile)

    $white = [System.Drawing.Color]::White
    $bodyW = $size * 0.50; $bodyH = $size * 0.36
    $bodyX = ($size - $bodyW) / 2; $bodyY = $size * 0.47
    $body = New-RoundedPath $bodyX $bodyY $bodyW $bodyH ([Math]::Max(1, $size * 0.07))
    $g.FillPath((New-Object System.Drawing.SolidBrush $white), $body)

    $stroke = [Math]::Max(1.6, $size * 0.085)
    $pen = New-Object System.Drawing.Pen $white, $stroke
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $shackleW = $bodyW * 0.62
    $shackleX = ($size - $shackleW) / 2
    $lift = if ($open) { $size * 0.10 } else { 0 }
    $shackleTop = $size * 0.20 - $lift
    $arcRect = New-Object System.Drawing.RectangleF $shackleX, $shackleTop, $shackleW, ($shackleW)
    $g.DrawArc($pen, $arcRect, 180, 180)
    $legBottom = $bodyY + 0.5
    $legTop = $shackleTop + $shackleW / 2
    $g.DrawLine($pen, $shackleX + $shackleW, $legTop, $shackleX + $shackleW, $legBottom - $lift)
    if (-not $open) { $g.DrawLine($pen, $shackleX, $legTop, $shackleX, $legBottom) }
    else { $g.DrawLine($pen, $shackleX, $legTop, $shackleX, $legTop + $size * 0.04) }

    if ($size -ge 24) {
        $holeR = $size * 0.045
        $cx = $size / 2; $cy = $bodyY + $bodyH * 0.45
        $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($bottom))), $cx - $holeR, $cy - $holeR, 2 * $holeR, 2 * $holeR)
    }

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

function Write-Icon([string]$path, [string]$top, [string]$bottom, [bool]$open, [int[]]$sizes) {
    $frames = foreach ($s in $sizes) { , (New-Frame $s $top $bottom $open) }
    $stream = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter $stream
    $w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $s = $sizes[$i]; $len = $frames[$i].Length
        $dim = if ($s -ge 256) { 0 } else { $s }
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([UInt16]1); $w.Write([UInt16]32); $w.Write([UInt32]$len); $w.Write([UInt32]$offset)
        $offset += $len
    }
    foreach ($f in $frames) { $w.Write($f) }
    $w.Flush(); $stream.Dispose()
    Write-Host "Wrote $path"
}

New-Item -ItemType Directory -Force $OutputDirectory | Out-Null
$all = @(16, 20, 24, 32, 40, 48, 64, 256)
$tray = @(16, 20, 24, 32, 40, 48, 64)
Write-Icon (Join-Path $OutputDirectory 'app.ico') '#6366F1' '#4338CA' $false $all
Write-Icon (Join-Path $OutputDirectory 'tray-working.ico') '#6366F1' '#4338CA' $false $tray
Write-Icon (Join-Path $OutputDirectory 'tray-ready.ico') '#FBBF24' '#D97706' $false $tray
Write-Icon (Join-Path $OutputDirectory 'tray-unlocked.ico') '#34D399' '#059669' $true $tray
Write-Icon (Join-Path $OutputDirectory 'tray-disabled.ico') '#9CA3AF' '#4B5563' $true $tray

# PNG logo for the WinUI app.
$logo = New-Frame 256 '#6366F1' '#4338CA' $false
[System.IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'logo-256.png'), $logo)
Write-Host "Wrote logo-256.png"
