<#
  卸载 MintWall：移除应用包、程序自己的默认数据目录（设置、壁纸库、缓存、缩略图、保存归档）
  和开机自启。用户在工作区里自选的壁纸库/保存目录不会被删除。
#>
$ErrorActionPreference = 'Continue'
$root = $PSScriptRoot

$pkg = Get-AppxPackage -Name 'MintWall.Desktop'
if ($pkg) {
    Write-Host '==> 移除应用包（包内数据由系统一并删除）'
    Remove-AppxPackage -Package $pkg.PackageFullName
}

$exe = Join-Path $root 'MintWall.exe'
if (Test-Path $exe) {
    Write-Host '==> 清理数据目录'
    Start-Process -FilePath $exe -ArgumentList '--uninstall' -Wait
}
else {
    foreach ($name in 'MintWall', 'WallpaperStudio') {
        $dir = Join-Path $env:LOCALAPPDATA $name
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "已删除：$dir"
        }
    }
}

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Remove-ItemProperty -Path $run -Name 'MintWall' -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $run -Name 'WallpaperStudio' -ErrorAction SilentlyContinue

$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
foreach ($name in '薄荷壁纸', 'MintWall') {
    Remove-Item (Join-Path $programs "$name.lnk") -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '完成：程序数据已清理，用户自选目录保持不动。'
Write-Host '便携版可以再手动删除本文件夹。'
