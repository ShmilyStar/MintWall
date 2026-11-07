<#
  Pulls makeappx.exe / signtool.exe out of the Microsoft.Windows.SDK.BuildTools NuGet package so MSIX
  packaging works without installing the Windows SDK.
#>
param(
    [Parameter(Mandatory)][string]$Destination,
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if (Test-Path (Join-Path $Destination 'makeappx.exe')) {
    Write-Host "打包工具已存在：$Destination"
    return
}

$index = Invoke-RestMethod 'https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/index.json'
$candidates = @($index.versions | Where-Object { $_ -notmatch '-' })
if ($Version) { $candidates = @($Version) }
if ($candidates.Count -eq 0) { throw 'NuGet 上没有可用的稳定版本' }
$target = $candidates[-1]

$url = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$target/microsoft.windows.sdk.buildtools.$target.nupkg"
$nupkg = Join-Path $env:TEMP "sdk-buildtools-$target.zip"
Write-Host "==> 下载 Microsoft.Windows.SDK.BuildTools $target"
Invoke-WebRequest $url -OutFile $nupkg

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($nupkg)
try {
    # .manifest 是 makeappx 的 SxS 依赖清单，漏拷会导致“并行配置不正确”而无法启动
    $wanted = $archive.Entries | Where-Object { $_.FullName -match '^bin/[^/]+/x64/[^/]+\.(exe|dll|manifest|ini)$' }
    if (-not $wanted) { throw '包内没有找到 bin/*/x64 工具' }
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    foreach ($entry in $wanted) {
        $outPath = Join-Path $Destination ([System.IO.Path]::GetFileName($entry.FullName))
        $stream = $entry.Open()
        $file = [System.IO.File]::Create($outPath)
        $stream.CopyTo($file)
        $file.Dispose()
        $stream.Dispose()
    }
}
finally {
    $archive.Dispose()
}

foreach ($tool in 'makeappx.exe', 'signtool.exe') {
    if (-not (Test-Path (Join-Path $Destination $tool))) { throw "解压后缺少 $tool" }
}

Write-Host "==> 工具就绪：$Destination"
