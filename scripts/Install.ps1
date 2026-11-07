$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$cer = Join-Path $root 'WallpaperStudio.cer'
$msix = Get-ChildItem $root -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $msix) { throw '安装包缺失：请把 .msix 与本脚本放在同一目录。' }

Write-Host "==> 安装包：$($msix.Name)"

if (Test-Path $cer) {
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -match 'WallpaperStudio' }
    if (-not $trusted) {
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Write-Host '==> 已把自签证书加入本机受信任人'
    }
    else {
        Write-Host '==> 证书已在受信任人列表，跳过'
    }
}
else {
    Write-Host '==> 未找到证书文件，直接尝试安装'
}

Write-Host '==> 注册应用包'
try {
    Add-AppxPackage -Path $msix.FullName -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}
catch {
    Write-Host ("首次安装失败：" + $_.Exception.Message)
    Write-Host '==> 尝试卸载旧版本后重装'
    Get-AppxPackage -Name 'WallpaperStudio.Desktop' | Remove-AppxPackage
    Add-AppxPackage -Path $msix.FullName
}

$pkg = Get-AppxPackage -Name 'WallpaperStudio.Desktop'
if (-not $pkg) { throw '安装未生效：系统里没有 WallpaperStudio.Desktop。' }

Write-Host ("==> 安装成功：" + $pkg.PackageFullName)
Write-Host ("==> 状态：" + $pkg.Status)

$appId = (Get-StartApps | Where-Object { $_.AppID -like "$($pkg.PackageFamilyName)*" } | Select-Object -First 1).AppID
if ($appId) {
    Start-Process explorer.exe -ArgumentList "shell:AppsFolder\$appId"
    Write-Host '==> 已启动 Wallpaper Studio'
}
