param(
    [string]$Source = 'E:\Agent\Qoder\WallpaperStudio\src\WallpaperStudio\Assets\Logo1024.png',
    [string]$Target = 'E:\Agent\Qoder\WallpaperStudio\src\WallpaperStudio\Assets\app.ico'
)
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$raw = [System.Drawing.Image]::FromFile($Source)
$blobs = @()
foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.DrawImage($raw, 0, 0, $s, $s)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $blobs += ,$ms.ToArray()
    $ms.Dispose()
}
$raw.Dispose()

$fs = [System.IO.File]::Create($Target)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($blob in $blobs) { $bw.Write($blob) }
$bw.Flush()
$bw.Dispose()
Write-Output ("wrote $Target ({0:N0} bytes, {1} sizes)" -f (Get-Item $Target).Length, $sizes.Count)
