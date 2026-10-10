<#
.SYNOPSIS
Create a multi-resolution app.ico from transparent assets\icon.png.
.DESCRIPTION
Store 16, 24, 32, 48, 64, 128, and 256 px PNG frames in one ICO.
Use System.Drawing and release owned bitmaps, streams, and GDI resources
in finally blocks. Preserve the original PNG.
.EXAMPLE
powershell -NoProfile -ExecutionPolicy Bypass -File .\make-icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$image = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot 'assets\icon.png'))
$frames = @()
try {
    foreach ($size in @(16,24,32,48,64,128,256)) {
        $bitmap = New-Object System.Drawing.Bitmap $size,$size
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $buffer = New-Object System.IO.MemoryStream
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($image, 0, 0, $size, $size)
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += @{ Size = $size; Data = $buffer.ToArray() }
        } finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
    $stream = [System.IO.File]::Create((Join-Path $PSScriptRoot 'app.ico'))
    $writer = New-Object System.IO.BinaryWriter $stream
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
        # ICO uses a 6-byte header and a 16-byte directory entry per frame.
        $offset = 6 + 16 * $frames.Count
        foreach ($frame in $frames) {
            # A zero ICO dimension byte represents 256 pixels.
            $writer.Write([byte]($frame.Size % 256)); $writer.Write([byte]($frame.Size % 256))
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frame.Data.Length); $writer.Write([uint32]$offset)
            $offset += $frame.Data.Length
        }
        foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Data) }
    } finally { $writer.Dispose(); $stream.Dispose() }
} finally { $image.Dispose() }
