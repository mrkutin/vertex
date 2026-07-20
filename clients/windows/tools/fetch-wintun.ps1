<#
.SYNOPSIS
  Downloads WinTUN 0.14.1 and stages the architecture-specific
  `wintun.dll` for both Phase 1 dev (Service runtimes) and Phase 5 MSI
  packaging.

.DESCRIPTION
  WinTUN ships as a zip from the WireGuard project. SHA256 is pinned;
  if the upstream artifact ever changes the script aborts before any
  unzip — kernel-level driver bytes must not be MITM-able.

  Output layout:
    src/Vertex.Service/runtimes/win-x64/native/wintun.dll       (Phase 1 dev publish)
    src/Vertex.Service/runtimes/win-arm64/native/wintun.dll
    src/Vertex.Service/runtimes/win-x86/native/wintun.dll
    packaging/Files/wintun-amd64.dll                            (Phase 5 MSI)
    packaging/Files/wintun-arm64.dll
    packaging/Files/LICENSE.txt                                 (MIT, must redistribute)
#>

[CmdletBinding()]
param(
    [string]$Version    = '0.14.1',
    [string]$Url        = 'https://www.wintun.net/builds/wintun-0.14.1.zip',
    [string]$Sha256     = '07c256185d6ee3652e09fa55c0b673e2624b565e02c4b9091c79ca7d2f24ef51'
)

$ErrorActionPreference = 'Stop'
$here     = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $here

$serviceRuntimes = Join-Path $repoRoot 'src\Vertex.Service\runtimes'
$packagingFiles  = Join-Path $repoRoot 'packaging\Files'
$tempZip         = Join-Path $env:TEMP 'wintun.zip'
$tempExtract     = Join-Path $env:TEMP 'wintun-extract'

Write-Host "→ Downloading WinTUN $Version from $Url"
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest -Uri $Url -OutFile $tempZip -UseBasicParsing

Write-Host '→ Verifying SHA256'
$got = (Get-FileHash -Algorithm SHA256 -Path $tempZip).Hash
if ($got -ne $Sha256) {
    Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
    throw "WinTUN zip SHA256 mismatch. Expected $Sha256, got $got. Refusing to install kernel driver bytes."
}

Write-Host '→ Extracting'
if (Test-Path $tempExtract) { Remove-Item $tempExtract -Recurse -Force }
Expand-Archive -Path $tempZip -DestinationPath $tempExtract -Force

# Layout inside the zip (per WireGuard docs): wintun/bin/{amd64,arm,arm64,x86}/wintun.dll
$mappings = @(
    @{ src = 'wintun/bin/amd64/wintun.dll'; rid = 'win-x64'   },
    @{ src = 'wintun/bin/arm64/wintun.dll'; rid = 'win-arm64' },
    @{ src = 'wintun/bin/x86/wintun.dll';   rid = 'win-x86'   }
)

foreach ($m in $mappings) {
    $sourceDll = Join-Path $tempExtract $m.src
    if (-not (Test-Path $sourceDll)) {
        Write-Warning "Missing $($m.src) in zip - skipping $($m.rid)"
        continue
    }
    $destDir = Join-Path $serviceRuntimes "$($m.rid)\native"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item -Path $sourceDll -Destination (Join-Path $destDir 'wintun.dll') -Force
    Write-Host "  $($m.rid)\native\wintun.dll  ($((Get-Item $sourceDll).Length) bytes)"
}

# Phase 5 MSI side-channel: rename amd64 -> wintun-amd64.dll and arm64
# -> wintun-arm64.dll under packaging/Files/. The wixproj picks the
# right one at install time via the VertexPlatform preprocessor switch.
New-Item -ItemType Directory -Force -Path $packagingFiles | Out-Null
$msiMappings = @(
    @{ src = 'wintun/bin/amd64/wintun.dll'; out = 'wintun-amd64.dll' },
    @{ src = 'wintun/bin/arm64/wintun.dll'; out = 'wintun-arm64.dll' }
)
foreach ($m in $msiMappings) {
    $sourceDll = Join-Path $tempExtract $m.src
    if (-not (Test-Path $sourceDll)) {
        Write-Warning "Missing $($m.src) in zip - MSI for that arch will fail to build"
        continue
    }
    Copy-Item -Path $sourceDll -Destination (Join-Path $packagingFiles $m.out) -Force
    Write-Host "  packaging/Files/$($m.out)  ($((Get-Item $sourceDll).Length) bytes)"
}

# WinTUN is MIT — redistribute LICENSE.txt alongside the DLLs.
$licenseSrc = Join-Path $tempExtract 'wintun/LICENSE.txt'
if (Test-Path $licenseSrc) {
    Copy-Item -Path $licenseSrc -Destination (Join-Path $packagingFiles 'LICENSE.txt') -Force
    Write-Host '  packaging/Files/LICENSE.txt'
}

Remove-Item $tempZip -Force -ErrorAction SilentlyContinue
Remove-Item $tempExtract -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "WinTUN $Version staged under src/Vertex.Service/runtimes/ and packaging/Files/"
