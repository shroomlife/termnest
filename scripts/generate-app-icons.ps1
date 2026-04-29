# Generates transparent Windows app icon assets for the packaged WinUI app.
# Windows uses the targetsize-* altform-unplated images for Taskbar/Start.

$ErrorActionPreference = 'Stop'

try {
    Add-Type -AssemblyName System.Drawing.Common
} catch {
    Add-Type -AssemblyName System.Drawing
}

$root = Resolve-Path "$PSScriptRoot\.."
$assets = Join-Path $root 'src\TermNest.App\Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null

function Get-Color([string]$hex) {
    return [System.Drawing.ColorTranslator]::FromHtml($hex)
}

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = [Math]::Max(1.0, $radius * 2.0)
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $w - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $w - $diameter, $y + $h - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $h - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$width, [int]$height, [string]$variant) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

    $base = [Math]::Min($width, $height)
    $boxWidth = [Math]::Round($base * 0.74)
    $boxHeight = [Math]::Round($base * 0.58)
    $x = [Math]::Round(($width - $boxWidth) / 2.0)
    $y = [Math]::Round(($height - $boxHeight) / 2.0)
    $radius = [Math]::Max(2, [Math]::Round($base * 0.095))
    $stroke = [Math]::Max(1.35, $base * 0.055)

    if ($variant -eq 'dark') {
        $fill = Get-Color '#F8FAFC'
        $border = Get-Color '#CBD5E1'
        $prompt = Get-Color '#15803D'
        $cursor = Get-Color '#111827'
        $shine = Get-Color '#FFFFFF'
    } else {
        $fill = Get-Color '#111827'
        $border = Get-Color '#64748B'
        $prompt = Get-Color '#22C55E'
        $cursor = Get-Color '#F8FAFC'
        $shine = Get-Color '#334155'
    }

    $path = New-RoundedRectPath $x $y $boxWidth $boxHeight $radius
    $fillBrush = [System.Drawing.SolidBrush]::new($fill)
    $borderPen = [System.Drawing.Pen]::new($border, $stroke)
    $graphics.FillPath($fillBrush, $path)
    $graphics.DrawPath($borderPen, $path)

    if ($base -ge 32) {
        $shinePen = [System.Drawing.Pen]::new($shine, [Math]::Max(1.0, $stroke * 0.45))
        $graphics.DrawLine($shinePen, $x + ($boxWidth * 0.16), $y + ($boxHeight * 0.24), $x + ($boxWidth * 0.84), $y + ($boxHeight * 0.24))
        $shinePen.Dispose()
    }

    $promptPen = [System.Drawing.Pen]::new($prompt, [Math]::Max(1.4, $stroke * 0.82))
    $promptPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $promptPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cursorPen = [System.Drawing.Pen]::new($cursor, [Math]::Max(1.4, $stroke * 0.82))
    $cursorPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $cursorPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    $px = $x + ($boxWidth * 0.25)
    $py = $y + ($boxHeight * 0.60)
    $glyph = [Math]::Max(4.0, $base * 0.15)
    $graphics.DrawLine($promptPen, $px, $py - ($glyph * 0.55), $px + ($glyph * 0.62), $py)
    $graphics.DrawLine($promptPen, $px + ($glyph * 0.62), $py, $px, $py + ($glyph * 0.55))
    $graphics.DrawLine($cursorPen, $px + ($glyph * 1.12), $py + ($glyph * 0.58), $px + ($glyph * 2.0), $py + ($glyph * 0.58))

    $cursorPen.Dispose()
    $promptPen.Dispose()
    $borderPen.Dispose()
    $fillBrush.Dispose()
    $path.Dispose()
    $graphics.Dispose()

    return $bitmap
}

function Save-Png([string]$fileName, [int]$width, [int]$height, [string]$variant = 'default') {
    $path = Join-Path $assets $fileName
    $bitmap = New-IconBitmap $width $height $variant
    try {
        $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $bitmap.Dispose()
    }
}

function New-PngBytes([int]$size) {
    $bitmap = New-IconBitmap $size $size 'default'
    $stream = [System.IO.MemoryStream]::new()
    try {
        $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return ,$stream.ToArray()
    } finally {
        $stream.Dispose()
        $bitmap.Dispose()
    }
}

function Save-Ico([string]$fileName, [int[]]$sizes) {
    $path = Join-Path $assets $fileName
    $entries = @()
    foreach ($size in $sizes) {
        $entries += [pscustomobject]@{
            Size = $size
            Bytes = New-PngBytes $size
        }
    }

    $stream = [System.IO.File]::Create($path)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$entries.Count)

        $offset = 6 + ($entries.Count * 16)
        foreach ($entry in $entries) {
            $dimension = if ($entry.Size -ge 256) { 0 } else { [byte]$entry.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$entry.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $entry.Bytes.Length
        }

        foreach ($entry in $entries) {
            $writer.Write($entry.Bytes)
        }
    } finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

$targetSizes = @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)
foreach ($size in $targetSizes) {
    Save-Png "Square44x44Logo.targetsize-$size.png" $size $size 'default'
    Save-Png "Square44x44Logo.targetsize-$($size)_altform-unplated.png" $size $size 'dark'
    Save-Png "Square44x44Logo.targetsize-$($size)_altform-lightunplated.png" $size $size 'default'
}

$square44Scales = @{
    'Square44x44Logo.png' = 44
    'Square44x44Logo.scale-100.png' = 44
    'Square44x44Logo.scale-125.png' = 55
    'Square44x44Logo.scale-150.png' = 66
    'Square44x44Logo.scale-200.png' = 88
    'Square44x44Logo.scale-400.png' = 176
}
foreach ($item in $square44Scales.GetEnumerator()) {
    Save-Png $item.Key $item.Value $item.Value 'default'
}

Save-Png 'Square150x150Logo.png' 150 150 'default'
Save-Png 'Square150x150Logo.scale-100.png' 150 150 'default'
Save-Png 'Square150x150Logo.scale-200.png' 300 300 'default'
Save-Png 'Wide310x150Logo.png' 310 150 'default'
Save-Png 'Wide310x150Logo.scale-200.png' 620 300 'default'
Save-Png 'SplashScreen.png' 620 300 'default'
Save-Png 'SplashScreen.scale-200.png' 1240 600 'default'
Save-Png 'StoreLogo.png' 50 50 'default'
Save-Png 'StoreLogo.scale-100.png' 50 50 'default'
Save-Png 'StoreLogo.scale-125.png' 63 63 'default'
Save-Png 'StoreLogo.scale-150.png' 75 75 'default'
Save-Png 'StoreLogo.scale-200.png' 100 100 'default'
Save-Png 'StoreLogo.scale-400.png' 200 200 'default'
Save-Png 'LockScreenLogo.scale-200.png' 48 48 'dark'

Save-Ico 'AppIcon.ico' @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 128, 256)

Write-Host "Generated transparent app icon assets in $assets" -ForegroundColor Green
