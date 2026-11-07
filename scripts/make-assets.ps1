param([string]$Source)
Add-Type -AssemblyName System.Drawing

$dest = 'E:\Agent\Qoder\WallpaperStudio\src\WallpaperStudio\Assets'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# 先复制到内存再释放文件句柄，否则最后回写同名 Logo1024.png 会报 GDI+ 错误
$raw = [System.Drawing.Image]::FromFile($Source)
$src = New-Object System.Drawing.Bitmap $raw
$raw.Dispose()
$targets = @(
    @{ Name = 'Square44x44Logo.png'; W = 44; H = 44 },
    @{ Name = 'Square88x88Logo.png'; W = 88; H = 88 },
    @{ Name = 'Square150x150Logo.png'; W = 150; H = 150 },
    @{ Name = 'Square310x310Logo.png'; W = 310; H = 310 },
    @{ Name = 'Wide310x150Logo.png'; W = 310; H = 150 },
    @{ Name = 'StoreLogo.png'; W = 50; H = 50 },
    @{ Name = 'Logo1024.png'; W = 1024; H = 1024 }
)

foreach ($t in $targets) {
    $bmp = New-Object System.Drawing.Bitmap $t.W, $t.H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    if ($t.Name -eq 'Wide310x150Logo.png') {
        # crop center square then fill the wide tile
        $side = [Math]::Min($src.Width, $src.Height)
        $x = [int](($src.Width - $side) / 2)
        $y = [int](($src.Height - $side) / 2)
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $t.W, $t.H), (New-Object System.Drawing.Rectangle $x, $y, $side, $side), [System.Drawing.GraphicsUnit]::Pixel)
    }
    else {
        $g.DrawImage($src, (New-Object System.Drawing.Rectangle 0, 0, $t.W, $t.H), (New-Object System.Drawing.Rectangle 0, 0, $src.Width, $src.Height), [System.Drawing.GraphicsUnit]::Pixel)
    }
    $g.Dispose()
    $path = Join-Path $dest $t.Name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ('wrote ' + $t.Name + ' ' + $t.W + 'x' + $t.H)
}
$src.Dispose()
