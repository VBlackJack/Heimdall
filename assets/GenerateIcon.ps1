<#
.SYNOPSIS
    Generates Heimdall application icon (multi-size .ico) using System.Drawing.
#>
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 32, 48, 64, 128, 256)
$outputPath = Join-Path $PSScriptRoot '..\src\Heimdall.App\app.ico'

function New-HeimdallIcon {
    param([int]$Size)

    $bmp = [System.Drawing.Bitmap]::new($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = $Size  # shorthand
    $cx = $s / 2
    $cy = $s / 2

    # Shield gradient background
    $shieldBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Point]::new(0, 0),
        [System.Drawing.Point]::new(0, $s),
        [System.Drawing.Color]::FromArgb(155, 93, 229),  # #9B5DE5
        [System.Drawing.Color]::FromArgb(124, 58, 237))  # #7C3AED

    # Shield path (pentagon-like)
    $shieldPoints = @(
        [System.Drawing.PointF]::new($cx, $s * 0.05),           # top center
        [System.Drawing.PointF]::new($s * 0.9, $s * 0.2),       # top right
        [System.Drawing.PointF]::new($s * 0.85, $s * 0.65),     # mid right
        [System.Drawing.PointF]::new($cx, $s * 0.95),           # bottom center
        [System.Drawing.PointF]::new($s * 0.15, $s * 0.65),     # mid left
        [System.Drawing.PointF]::new($s * 0.1, $s * 0.2)        # top left
    )
    $g.FillPolygon($shieldBrush, $shieldPoints)

    # Inner shield (dark)
    $innerBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(30, 30, 46))  # #1E1E2E
    $innerMargin = $s * 0.08
    $innerPoints = @(
        [System.Drawing.PointF]::new($cx, $s * 0.12),
        [System.Drawing.PointF]::new($s * 0.83, $s * 0.25),
        [System.Drawing.PointF]::new($s * 0.79, $s * 0.62),
        [System.Drawing.PointF]::new($cx, $s * 0.88),
        [System.Drawing.PointF]::new($s * 0.21, $s * 0.62),
        [System.Drawing.PointF]::new($s * 0.17, $s * 0.25)
    )
    $g.FillPolygon($innerBrush, $innerPoints)

    # Eye shape (Heimdall's eye)
    $eyePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(124, 58, 237), [Math]::Max(1, $s * 0.02))
    $eyeW = $s * 0.35
    $eyeH = $s * 0.2
    $eyeY = $s * 0.32
    $g.DrawEllipse($eyePen, $cx - $eyeW/2, $eyeY, $eyeW, $eyeH)

    # Pupil
    $pupilBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(124, 58, 237))
    $pupilR = $s * 0.06
    $g.FillEllipse($pupilBrush, $cx - $pupilR, $eyeY + $eyeH/2 - $pupilR, $pupilR * 2, $pupilR * 2)

    # Inner pupil (white)
    $whiteBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, 240, 240))
    $innerR = $s * 0.025
    $g.FillEllipse($whiteBrush, $cx - $innerR, $eyeY + $eyeH/2 - $innerR, $innerR * 2, $innerR * 2)

    # Three connection dots (SSH green, RDP blue, SFTP amber)
    $dotR = $s * 0.025
    $dotY = $s * 0.62
    $sshBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(34, 197, 94))
    $rdpBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(59, 130, 246))
    $sftpBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(245, 158, 11))

    $g.FillEllipse($sshBrush, $cx - $s * 0.18 - $dotR, $dotY - $dotR, $dotR * 2, $dotR * 2)
    $g.FillEllipse($rdpBrush, $cx - $dotR, $dotY - $dotR, $dotR * 2, $dotR * 2)
    $g.FillEllipse($sftpBrush, $cx + $s * 0.18 - $dotR, $dotY - $dotR, $dotR * 2, $dotR * 2)

    # Terminal prompt "H>_" if large enough
    if ($s -ge 48) {
        $fontSize = [Math]::Max(8, $s * 0.12)
        $font = [System.Drawing.Font]::new('Consolas', $fontSize, [System.Drawing.FontStyle]::Bold)
        $textBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(240, 240, 240))
        $textY = $s * 0.68
        $textSize = $g.MeasureString('H>_', $font)
        $g.DrawString('H>_', $font, $textBrush, $cx - $textSize.Width / 2, $textY)
        $font.Dispose()
        $textBrush.Dispose()
    }

    $shieldBrush.Dispose()
    $innerBrush.Dispose()
    $eyePen.Dispose()
    $pupilBrush.Dispose()
    $whiteBrush.Dispose()
    $sshBrush.Dispose()
    $rdpBrush.Dispose()
    $sftpBrush.Dispose()
    $g.Dispose()

    return $bmp
}

# Generate multi-size ICO
$ms = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($ms)

# ICO header
$writer.Write([uint16]0)        # Reserved
$writer.Write([uint16]1)        # Type (ICO)
$writer.Write([uint16]$sizes.Count) # Image count

# Generate all bitmaps first
$bitmaps = @()
$pngDatas = @()
foreach ($sz in $sizes) {
    $bmp = New-HeimdallIcon -Size $sz
    $bitmaps += $bmp
    $pngMs = [System.IO.MemoryStream]::new()
    $bmp.Save($pngMs, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDatas += ,($pngMs.ToArray())
    $pngMs.Dispose()
}

# Write directory entries
$dataOffset = 6 + ($sizes.Count * 16)  # header + entries
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $data = $pngDatas[$i]
    $writer.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))  # Width
    $writer.Write([byte]$(if ($sz -ge 256) { 0 } else { $sz }))  # Height
    $writer.Write([byte]0)       # Color palette
    $writer.Write([byte]0)       # Reserved
    $writer.Write([uint16]1)     # Color planes
    $writer.Write([uint16]32)    # Bits per pixel
    $writer.Write([uint32]$data.Length)  # Data size
    $writer.Write([uint32]$dataOffset)  # Data offset
    $dataOffset += $data.Length
}

# Write PNG data
foreach ($data in $pngDatas) {
    $writer.Write($data)
}

$writer.Flush()
[System.IO.File]::WriteAllBytes($outputPath, $ms.ToArray())

$writer.Dispose()
$ms.Dispose()
foreach ($bmp in $bitmaps) { $bmp.Dispose() }

Write-Host "Icon generated: $outputPath ($($sizes.Count) sizes: $($sizes -join ', ')px)"
