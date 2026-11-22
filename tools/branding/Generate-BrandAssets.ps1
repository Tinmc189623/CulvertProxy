# 生成统一的 CulvertProxy 品牌图标：网站、Wiki、应用、任务栏和 MSIX 共用同一套几何。
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$appAssets = Join-Path $root 'src\Nexsteaduser.CulvertProxy.App\Assets'
$docsAssets = Join-Path $root 'docs\assets'
$msixAssets = Join-Path $root 'installer\msix\Assets'
foreach ($dir in @($appAssets, $docsAssets, $msixAssets)) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function New-RoundedPath([float]$x, [float]$y, [float]$width, [float]$height, [float]$radius) {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-ArchPath {
    $path = [Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddLine(76, 220, 76, 132)
    $path.AddArc(76, 80, 104, 104, 180, 180)
    $path.AddLine(180, 132, 180, 220)
    $path.CloseFigure()
    return $path
}

function New-IconBitmap([int]$size) {
    $supersample = 4
    $canvas = $size * $supersample
    $big = [Drawing.Bitmap]::new($canvas, $canvas, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($big)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)
    $scale = $canvas / 256.0
    $g.ScaleTransform($scale, $scale)

    $tilePath = New-RoundedPath 12 12 232 232 54
    $tileBrush = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.PointF]::new(18, 18),
        [Drawing.PointF]::new(238, 238),
        [Drawing.Color]::FromArgb(255, 19, 183, 163),
        [Drawing.Color]::FromArgb(255, 7, 129, 119))
    $g.FillPath($tileBrush, $tilePath)
    $tileBrush.Dispose()

    if ($size -ge 32) {
        $borderPath = New-RoundedPath 14 14 228 228 52
        $borderPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(46, 255, 255, 255), 2)
        $g.DrawPath($borderPen, $borderPath)
        $borderPen.Dispose()
        $borderPath.Dispose()
    }

    $arch = New-ArchPath
    $ink = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 7, 17, 15))
    $g.FillPath($ink, $arch)
    $ink.Dispose()
    $arch.Dispose()

    $packet = New-RoundedPath 104 160 48 18 9
    $amber = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 241, 180, 92))
    $g.FillPath($amber, $packet)
    $amber.Dispose()
    $packet.Dispose()

    $g.Dispose()
    $tilePath.Dispose()

    $result = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g2 = [Drawing.Graphics]::FromImage($result)
    $g2.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g2.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g2.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g2.DrawImage($big, 0, 0, $size, $size)
    $g2.Dispose()
    $big.Dispose()
    return $result
}

function Save-IconPng([int]$size, [string]$path) {
    $bitmap = New-IconBitmap $size
    $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
}

function Write-MultiSizeIco([string]$path) {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $frames = foreach ($size in $sizes) {
        $bitmap = New-IconBitmap $size
        $stream = [IO.MemoryStream]::new()
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
        [pscustomobject]@{ Size = $size; Bytes = $stream.ToArray() }
        $stream.Dispose()
    }

    $file = [IO.File]::Create($path)
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([UInt16]0)
        $writer.Write([UInt16]1)
        $writer.Write([UInt16]$frames.Count)
        $offset = 6 + (16 * $frames.Count)
        foreach ($frame in $frames) {
            $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([UInt16]1)
            $writer.Write([UInt16]32)
            $writer.Write([UInt32]$frame.Bytes.Length)
            $writer.Write([UInt32]$offset)
            $offset += $frame.Bytes.Length
        }

        foreach ($frame in $frames) {
            $writer.Write($frame.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ('culvert-brand-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $temp | Out-Null
try {
    $master = Join-Path $temp 'master-256.png'
    Save-IconPng 256 $master
    Copy-Item $master (Join-Path $appAssets 'culvertproxy-mark.png') -Force
    Copy-Item $master (Join-Path $docsAssets 'culvertproxy-icon.png') -Force
    Save-IconPng 150 (Join-Path $msixAssets 'Square150x150Logo.png')
    Save-IconPng 44 (Join-Path $msixAssets 'Square44x44Logo.png')
    Save-IconPng 50 (Join-Path $msixAssets 'StoreLogo.png')

    Write-MultiSizeIco (Join-Path $appAssets 'culvert.ico')
    Copy-Item (Join-Path $appAssets 'culvert.ico') (Join-Path $docsAssets 'favicon.ico') -Force
    Copy-Item (Join-Path $appAssets 'culvert.ico') (Join-Path $root 'docs\favicon.ico') -Force
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host '品牌资产已生成：culvert.ico（9 帧）、应用 PNG、网站图标与 MSIX 图标。' -ForegroundColor Green
