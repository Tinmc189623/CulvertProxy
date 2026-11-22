# 构建 MSI 与 MSIX，并对便携包、安装包执行代码签名。
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertThumbprint = $env:CULVERT_SIGN_CERT_THUMBPRINT,
    [switch]$SkipPortableZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "artifacts\publish\win-x64"
$artifacts = Join-Path $root 'artifacts'
$version = '1.0.2'

if (-not (Test-Path (Join-Path $publishDir 'CulvertProxy.exe'))) {
    throw "缺少发布目录：$publishDir。请先运行 build.ps1 -Publish。"
}

$makeAppx = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\makeappx.exe'
$signTool = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\signtool.exe'
if (-not (Test-Path $makeAppx)) {
    $makeAppx = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}

if (-not (Test-Path $signTool)) {
    $signTool = (Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1).FullName
}

if (-not (Test-Path $makeAppx)) {
    throw '未找到 makeappx.exe。'
}

function Sign-File([string]$path) {
    if (Test-Path $signTool) {
        & $signTool sign /fd SHA256 /sha1 $CertThumbprint /tr http://timestamp.digicert.com /td SHA256 $path
        if ($LASTEXITCODE -ne 0) {
            Write-Warning "时间戳签名失败，改为本地签名：$path"
            & $signTool sign /fd SHA256 /sha1 $CertThumbprint $path
            if ($LASTEXITCODE -ne 0) {
                throw "签名失败：$path"
            }
        }

        Write-Host "已签名：$path" -ForegroundColor Green
    }
}

$signEnabled = -not [string]::IsNullOrWhiteSpace($CertThumbprint)
if ($signEnabled) {
    if (-not (Test-Path $signTool)) {
        throw '未找到 signtool.exe。'
    }

    foreach ($file in @('CulvertProxy.exe', 'cli\culvert.exe', 'CulvertHook.dll')) {
        $candidate = Join-Path $publishDir $file
        if (Test-Path $candidate) {
            Sign-File $candidate
        }
    }
}

if (-not $SkipPortableZip) {
    $zip = Join-Path $artifacts 'CulvertProxy-win-x64.zip'
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }

    Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip
}

$msi = Join-Path $artifacts "CulvertProxy-$version-x64.msi"
Write-Host '构建 MSI...' -ForegroundColor Cyan
wix build (Join-Path $PSScriptRoot 'wix\Product.wxs') `
    -arch x64 `
    -d "PublishDir=$publishDir" `
    -d "ProductVersion=$version" `
    -o $msi
if ($LASTEXITCODE -ne 0) {
    throw 'MSI 构建失败。'
}

$msix = Join-Path $artifacts "CulvertProxy-$version-x64.msix"
$stage = Join-Path $artifacts "msix\stage-$version"
if (Test-Path $stage) {
    Remove-Item $stage -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item (Join-Path $publishDir '*') $stage -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot 'msix\AppxManifest.xml') $stage -Force
Copy-Item (Join-Path $PSScriptRoot 'msix\Assets') $stage -Recurse -Force

Write-Host '构建 MSIX...' -ForegroundColor Cyan
& $makeAppx pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) {
    throw 'MSIX 构建失败。'
}

if ($signEnabled) {
    Sign-File $msi
    Sign-File $msix
}

$hashLines = @()
foreach ($file in @($msi, $msix, (Join-Path $artifacts 'CulvertProxy-win-x64.zip'))) {
    if (Test-Path $file) {
        $hash = (Get-FileHash $file -Algorithm SHA256).Hash
        $hashLines += "$hash  $([IO.Path]::GetFileName($file))"
    }
}

$hashFile = Join-Path $artifacts "CulvertProxy-$version-SHA256SUMS.txt"
Set-Content -Path $hashFile -Value $hashLines -Encoding ascii
Get-Content $hashFile

Write-Host "MSI：$msi" -ForegroundColor Green
Write-Host "MSIX：$msix" -ForegroundColor Green
Write-Host "校验文件：$hashFile" -ForegroundColor Green
