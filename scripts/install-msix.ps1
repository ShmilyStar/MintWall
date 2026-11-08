<#
  Manual install for the self-signed MSIX: trusts the packaging certificate, makes sure sideloading is
  allowed, then registers the package. Re-run after every rebuild.
#>
param(
    [string]$Package = '',
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
$cer = Join-Path $root 'packaging\MintWall.cer'

if (-not $Package) {
    $Package = (Get-ChildItem $dist -Filter *.msix | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}
if (-not $Package -or -not (Test-Path $Package)) { throw '找不到 .msix，请先运行 scripts\build.ps1' }

$name = (Get-Item $Package).BaseName
Write-Host "==> 安装包：$Package"

if ($Uninstall) {
    Get-AppxPackage -Name 'MintWall.Desktop' | Remove-AppxPackage
    Write-Host '==> 已卸载'
    return
}

# 1. AppX 只认本机（LocalMachine）受信任存储；自签根证书放进受信任人即可，无需改受信任根。
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw '安装 MSIX 需要管理员权限（证书要写入本机受信任人）。请右键 PowerShell 以管理员身份运行后重跑本脚本。'
}

if (Test-Path $cer) {
    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Subject -match 'MintWall' }
    if (-not $trusted) {
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
        Write-Host '==> 证书已加入本机受信任人'
    }
    else {
        Write-Host '==> 证书已在受信任列表中'
    }
}

# 2. Non-Store packages need sideloading or developer mode enabled.
$unlock = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
$sideload = $false
foreach ($value in 'AllowAllTrustedApps', 'AllowDevelopmentWithoutDevLicense') {
    if ((Get-ItemProperty -Path $unlock -Name $value -ErrorAction SilentlyContinue).$value -eq 1) { $sideload = $true }
}
if (-not $sideload) {
    try {
        New-Item -Path $unlock -Force | Out-Null
        New-ItemProperty -Path $unlock -Name AllowDevelopmentWithoutDevLicense -Value 1 -PropertyType DWord -Force | Out-Null
        Write-Host '==> 已开启开发者模式（免许可证侧载）'
    }
    catch {
        Write-Warning '未能自动开启侧载。请手动打开：设置 → 更新和安全 → 开发者使用 → 侧载，然后重跑本脚本。'
    }
}

# 3. Register the package.
try {
    Add-AppxPackage -Path $Package -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}
catch {
    Write-Host ''
    Write-Host "安装失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host '常见原因：证书未受信任（0x80073CFB）、未开启侧载（0x80073CFD）、签名者与清单不一致（0x80073CF9）。'
    exit 1
}

$pkg = Get-AppxPackage -Name 'MintWall.Desktop'
$aumid = "$($pkg.PackageFamilyName)!MintWall"

# 清理旧版遗留的开始菜单快捷方式：图标指向旧包目录会发白，右键也没有卸载项。
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
foreach ($old in '薄荷壁纸', 'MintWall') {
    Remove-Item (Join-Path $programs "$old.lnk") -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "==> 安装成功：$($pkg.PackageFullName)"
Write-Host "==> 启动标识：$aumid"
Write-Host "==> 命令行启动：explorer.exe ""shell:AppsFolder\$aumid"""
Write-Host '==> 开始菜单使用应用包自带入口（右键可卸载）'
