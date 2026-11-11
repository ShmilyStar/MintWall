<#
  Builds MintWall and produces a store-style MSIX package.
  Nothing here needs Visual Studio or a machine-wide Windows SDK: makeappx/signtool come from the
  Microsoft.Windows.SDK.BuildTools NuGet package, and the .NET SDK can live in .tools\dotnet.
#>
param(
    [string]$Version = '1.0.0',
    [switch]$SkipMsix,
    [switch]$SkipTools
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MintWall\MintWall.csproj'
$manifest = Join-Path $root 'packaging\AppxManifest.xml'
$assets = Join-Path $root 'src\MintWall\Assets'
$build = Join-Path $root 'build'
$publish = Join-Path $build 'publish'
$staging = Join-Path $build 'staging'
$dist = Join-Path $root 'dist'
$toolsRoot = Join-Path (Split-Path -Parent $root) '.tools'

function Find-DotNet {
    $local = Join-Path $toolsRoot 'dotnet\dotnet.exe'
    if (Test-Path $local) { return $local }
    $global = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($global) { return $global.Source }
    throw '找不到 .NET SDK。请先运行 scripts\install-sdk.ps1，或安装 .NET 8 SDK。'
}

$dotnet = Find-DotNet
Write-Host "==> 使用 dotnet: $dotnet"

if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging, $publish, $dist | Out-Null

Write-Host '==> 发布 self-contained win-x64'
& $dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$LASTEXITCODE）" }

Write-Host '==> 准备打包目录'
Copy-Item -Path (Join-Path $publish '*') -Destination $staging -Recurse -Force
# 清单版本跟随 -Version，避免包版本与程序版本不一致
$manifestXml = [System.IO.File]::ReadAllText($manifest, [System.Text.Encoding]::UTF8)
$msixVersion = (@($Version.Split('.') + '0', '0', '0')[0..3]) -join '.'
$manifestXml = $manifestXml -replace '(?<=<Identity\s[^>]*Version=")[^"]+', $msixVersion
# makeappx 不接受带 BOM 的清单，必须写无 BOM 的 UTF-8
[System.IO.File]::WriteAllText((Join-Path $staging 'AppxManifest.xml'), $manifestXml, (New-Object System.Text.UTF8Encoding($false)))
if (-not (Test-Path $assets)) { throw "缺少图标资源目录 $assets，请先运行 scripts\make-assets.ps1" }
$stagingAssets = Join-Path $staging 'Assets'
New-Item -ItemType Directory -Force -Path $stagingAssets | Out-Null
Copy-Item -Path (Join-Path $assets '*') -Destination $stagingAssets -Force
# 包名跟系统语言走：清单里写 ms-resource 引用，真正的中/英文本放在 Strings 下编进 resources.pri
Copy-Item -Path (Join-Path $root 'packaging\Strings') -Destination $staging -Recurse -Force

if (-not $SkipMsix) {
    Write-Host '==> 编译开机启动壳（startupTask 不支持参数，壳负责传 --background）'
    $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path $csc)) { throw "找不到 .NET Framework 编译器：$csc" }
    & $csc /nologo /target:winexe /optimize+ /platform:anycpu "/out:$(Join-Path $staging 'MintWallStartup.exe')" (Join-Path $root 'src\MintWall.Startup\Program.cs')
    if ($LASTEXITCODE -ne 0) { throw "编译 MintWallStartup.exe 失败（$LASTEXITCODE）" }

    $toolDir = Join-Path $toolsRoot 'msix-tools'
    if (-not $SkipTools -or -not (Test-Path (Join-Path $toolDir 'makeappx.exe'))) {
        Write-Host '==> 获取 makeappx / signtool（来自 Microsoft.Windows.SDK.BuildTools）'
        & (Join-Path $PSScriptRoot 'get-pack-tools.ps1') -Destination $toolDir
    }

    $makeappx = Join-Path $toolDir 'makeappx.exe'
    $makepri = Join-Path $toolDir 'makepri.exe'
    $signtool = Join-Path $toolDir 'signtool.exe'
    $msix = Join-Path $dist "MintWall_${Version}_x64.msix"

    Write-Host '==> makepri 生成本地化 resources.pri'
    & $makepri new /pr $staging /cf (Join-Path $root 'packaging\priconfig.xml') /of (Join-Path $staging 'resources.pri') /mn (Join-Path $staging 'AppxManifest.xml') /o
    if ($LASTEXITCODE -ne 0) { throw "makepri 失败（$LASTEXITCODE）" }

    Write-Host "==> makeappx pack -> $msix"
    & $makeappx pack /d $staging /p $msix /o /v
    if ($LASTEXITCODE -ne 0) { throw "makeappx 失败（$LASTEXITCODE）" }

    Write-Host '==> 签名'
    $certScript = Join-Path $PSScriptRoot 'make-cert.ps1'
    $thumb = (& $certScript | Select-Object -Last 1)
    if (-not $thumb) { throw '未能取得签名证书指纹' }

    & $signtool sign /fd SHA256 /sha1 $thumb /tr http://timestamp.digicert.com /td SHA256 $msix
    if ($LASTEXITCODE -ne 0) {
        Write-Warning '时间戳服务器不可达，改用无时间戳签名（本机安装仍可用，跨机器需注意有效期）'
        & $signtool sign /fd SHA256 /sha1 $thumb $msix
        if ($LASTEXITCODE -ne 0) { throw 'signtool 签名失败' }
    }

    & $signtool verify /pa /v $msix | Out-Null
    Write-Host ("==> 包已生成：{0}（{1:N1} MB）" -f $msix, ((Get-Item $msix).Length / 1MB))

    Write-Host '==> 生成一键安装包（含证书，双击 Install.cmd 即可）'
    $installer = Join-Path $dist "MintWall_${Version}_installer_x64"
    if (Test-Path $installer) { Remove-Item $installer -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $installer | Out-Null
    Copy-Item $msix -Destination $installer
    Copy-Item (Join-Path $root 'packaging\MintWall.cer') -Destination $installer
    Copy-Item (Join-Path $PSScriptRoot 'Install.cmd'), (Join-Path $PSScriptRoot 'Install.ps1') -Destination $installer
    Copy-Item (Join-Path $PSScriptRoot 'Uninstall.cmd'), (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $installer
    $installerZip = "$installer.zip"
    if (Test-Path $installerZip) { Remove-Item $installerZip -Force }
    Compress-Archive -Path (Join-Path $installer '*') -DestinationPath $installerZip
    Write-Host ("==> 一键安装包：{0}（{1:N1} MB）" -f $installerZip, ((Get-Item $installerZip).Length / 1MB))
}

Write-Host '==> 发布单文件主程序（便携版与单文件资产共用）'
$singleFile = Join-Path $build 'singlefile'
if (Test-Path $singleFile) { Remove-Item $singleFile -Recurse -Force }
& $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o $singleFile --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "单文件 publish 失败（$LASTEXITCODE）" }
$exeAsset = Join-Path $dist "MintWall_${Version}_win-x64.exe"
Copy-Item (Join-Path $singleFile 'MintWall.exe') $exeAsset -Force
Write-Host ("==> 单文件主程序：{0}（{1:N1} MB）" -f $exeAsset, ((Get-Item $exeAsset).Length / 1MB))

Write-Host '==> 生成免安装便携版 zip（根目录：MintWall.exe + 预置 data 子文件夹）'
$zip = Join-Path $dist "MintWall_${Version}_portable_x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
$portable = Join-Path $build 'portable'
if (Test-Path $portable) { Remove-Item $portable -Recurse -Force }
New-Item -ItemType Directory -Force -Path $portable | Out-Null
Copy-Item $exeAsset (Join-Path $portable 'MintWall.exe') -Force
New-Item -ItemType Directory -Force -Path (Join-Path $portable 'data') | Out-Null
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath $zip
# Compress-Archive 会丢掉空目录，手动补上 data 目录条目
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Update)
try {
    if (-not ($archive.Entries | Where-Object { $_.FullName -eq 'data/' })) {
        $archive.CreateEntry('data/') | Out-Null
    }
} finally {
    $archive.Dispose()
}
Write-Host ("==> 便携包：{0}（{1:N1} MB）" -f $zip, ((Get-Item $zip).Length / 1MB))

Write-Host '完成。'
