<#
  Ensures a self-signed code-signing certificate exists for MSIX signing and prints its thumbprint on
  stdout (build.ps1 captures that value). Everything else goes to the host stream.
#>
param([string]$Subject = 'CN=MintWall Self-Signed', [int]$Years = 5)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cerPath = Join-Path $root 'packaging\MintWall.cer'

$existing = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $existing) {
    Write-Host "==> 创建自签名代码签名证书：$Subject"
    $existing = New-SelfSignedCertificate `
        -Subject $Subject `
        -Type CodeSigningCert `
        -CertStoreLocation Cert:\CurrentUser\My `
        -HashAlgorithm SHA256 `
        -KeyLength 2048 `
        -KeyUsage DigitalSignature `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
        -NotAfter (Get-Date).AddYears($Years)
}
else {
    Write-Host "==> 复用已有证书（有效期至 $($existing.NotAfter.ToString('yyyy-MM-dd'))）"
}

$publicBytes = $existing.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($cerPath, $publicBytes)
Write-Host "==> 公钥已导出：$cerPath"
Write-Host "==> 签名者：$Subject（必须与 AppxManifest.xml 的 Publisher 完全一致）"

$existing.Thumbprint
