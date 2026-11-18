# Draws the Simplemize mark (a fader turned all the way down) and writes:
#   assets\simplemize.ico   multi-size icon for the exe
#   docs\favicon.png        180 px for the website
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$root = Split-Path $PSScriptRoot -Parent

function New-MarkPng([int]$size) {
    $ink   = [Windows.Media.Color]::FromRgb(0x16, 0x15, 0x13)
    $paper = [Windows.Media.Color]::FromRgb(0xF1, 0xEF, 0xEA)
    $acc   = [Windows.Media.Color]::FromRgb(0xFF, 0x5A, 0x1F)
    $dv = New-Object Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()
    $s = [double]$size
    $r = $s * 0.19
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $ink), $null, (New-Object Windows.Rect 0, 0, $s, $s), $r, $r)
    $th = [Math]::Max(1.5, $s * 0.07)
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $paper), $null, (New-Object Windows.Rect ($s * 0.21), ($s * 0.5 - $th / 2), ($s * 0.58), $th), ($th / 2), ($th / 2))
    $k = $s * 0.26
    $dc.DrawRoundedRectangle((New-Object Windows.Media.SolidColorBrush $acc), $null, (New-Object Windows.Rect ($s * 0.19), ($s * 0.37), $k, $k), ($s * 0.04), ($s * 0.04))
    $dc.Close()
    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($sz in $sizes) { ,(New-MarkPng $sz) }

$out = New-Object IO.MemoryStream
$bw = New-Object IO.BinaryWriter $out
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $images[$i].Length
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($img in $images) { $bw.Write($img) }
$bw.Flush()
New-Item -ItemType Directory -Force (Join-Path $root 'assets') | Out-Null
[IO.File]::WriteAllBytes((Join-Path $root 'assets\simplemize.ico'), $out.ToArray())

New-Item -ItemType Directory -Force (Join-Path $root 'docs') | Out-Null
[IO.File]::WriteAllBytes((Join-Path $root 'docs\favicon.png'), (New-MarkPng 180))
"icon written"
