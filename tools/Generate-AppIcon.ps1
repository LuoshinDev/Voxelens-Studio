param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\ZhuJieJing.App\Assets'),
    [switch]$Avatar
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase
Add-Type -AssemblyName PresentationCore

# Layer geometry adapted from Lucide layers (ISC); see licenses/lucide.txt.
$layerGeometry = @(
    [System.Windows.Media.Geometry]::Parse('M12.83,2.18 a2,2 0 0 0 -1.66,0 L2.6,6.08 a1,1 0 0 0 0,1.83 l8.58,3.91 a2,2 0 0 0 1.66,0 l8.58,-3.9 a1,1 0 0 0 0,-1.83 z'),
    [System.Windows.Media.Geometry]::Parse('M2,12 a1,1 0 0 0 .58,.91 l8.6,3.91 a2,2 0 0 0 1.65,0 l8.58,-3.9 A1,1 0 0 0 22,12'),
    [System.Windows.Media.Geometry]::Parse('M2,17 a1,1 0 0 0 .58,.91 l8.6,3.91 a2,2 0 0 0 1.65,0 l8.58,-3.9 A1,1 0 0 0 22,17')
)

function Convert-Color([string]$Hex)
{
    return [System.Windows.Media.ColorConverter]::ConvertFromString($Hex)
}

function New-LogoPngBytes([int]$Size)
{
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    try
    {
        $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($Size / 1024.0, $Size / 1024.0))

        $tileBrush = [System.Windows.Media.LinearGradientBrush]::new(
            (Convert-Color '#F1E6FF'),
            (Convert-Color '#FAE4F6'),
            [System.Windows.Point]::new(0, 0),
            [System.Windows.Point]::new(1, 1))
        $borderWidth = [Math]::Max(28.0, 0.82 * 1024.0 / $Size)
        $tilePen = [System.Windows.Media.Pen]::new(
            [System.Windows.Media.SolidColorBrush]::new((Convert-Color '#DDCBFD')),
            $borderWidth)
        if($Avatar)
        {
            # Avatar consumers may flatten transparent corners against black.
            $drawing.DrawRectangle($tileBrush, $null, [System.Windows.Rect]::new(0, 0, 1024, 1024))
        }
        else
        {
            $drawing.DrawRoundedRectangle(
                $tileBrush,
                $tilePen,
                [System.Windows.Rect]::new(64, 64, 896, 896),
                224,
                224)
        }

        $drawing.PushTransform([System.Windows.Media.TranslateTransform]::new(260, 260))
        $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new(21, 21))
        $glyphWidth = [Math]::Max(2.0, 49.0 / $Size)
        $glyphPen = [System.Windows.Media.Pen]::new(
            [System.Windows.Media.SolidColorBrush]::new((Convert-Color '#7008E7')),
            $glyphWidth)
        $glyphPen.StartLineCap = [System.Windows.Media.PenLineCap]::Round
        $glyphPen.EndLineCap = [System.Windows.Media.PenLineCap]::Round
        $glyphPen.LineJoin = [System.Windows.Media.PenLineJoin]::Round
        foreach($geometry in $layerGeometry)
        {
            $drawing.DrawGeometry($null, $glyphPen, $geometry)
        }
        $drawing.Pop()
        $drawing.Pop()
        $drawing.Pop()
    }
    finally
    {
        $drawing.Close()
    }

    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new(
        $Size,
        $Size,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory = [System.IO.MemoryStream]::new()
    try
    {
        $encoder.Save($memory)
        return $memory.ToArray()
    }
    finally
    {
        $memory.Dispose()
    }
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null
$pngPath = Join-Path $resolvedOutput $(if($Avatar) { 'voxelens-studio-logo.png' } else { 'ZhuJieJing.png' })
$icoPath = Join-Path $resolvedOutput 'ZhuJieJing.ico'

[System.IO.File]::WriteAllBytes($pngPath, (New-LogoPngBytes 1024))
if($Avatar)
{
    Write-Output $pngPath
    return
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = foreach($size in $sizes)
{
    [pscustomobject]@{ Size = $size; Bytes = New-LogoPngBytes $size }
}

$file = [System.IO.File]::Create($icoPath)
$writer = [System.IO.BinaryWriter]::new($file)
try
{
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach($frame in $frames)
    {
        $dimension = if($frame.Size -eq 256) { [byte]0 } else { [byte]$frame.Size }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length)
        $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
}
finally
{
    $writer.Dispose()
    $file.Dispose()
}

Write-Output $pngPath
Write-Output $icoPath
