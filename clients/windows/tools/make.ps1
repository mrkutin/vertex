<#
.SYNOPSIS
  Vertex Windows client build helper. Analog of the project Makefile.
  Bumping SemVer + producing signed bits is mandatory per project rule
  ("binaries ONLY via Makefile").

.PARAMETER Target
  One of: restore, build, build-debug, test, clean, publish-amd64,
  publish-arm64, sign-bins-amd64, sign-bins-arm64, msi-amd64, msi-arm64,
  msi-all, all.

.PARAMETER VertexCertPfx
  Path to Authenticode signing certificate (.pfx). Optional — when set,
  sign-bins-* and msi-* embed an Authenticode signature on the produced
  binaries / MSI. Without it, signing is skipped (dev workflow).

.PARAMETER VertexCertPwd
  Password for the .pfx file. Required when VertexCertPfx is set.

.PARAMETER VertexTimestamper
  RFC 3161 timestamping URL. Defaults to DigiCert.

.EXAMPLE
  .\tools\make.ps1 build
  .\tools\make.ps1 publish-amd64
  .\tools\make.ps1 msi-amd64

  # From Mac via SSH (VM default shell is Windows PowerShell 5.1):
  ssh vertex-win 'cd \\Mac\vertex\clients\windows; .\tools\make.ps1 build'
#>

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('restore', 'build', 'build-debug', 'test', 'clean',
                 'publish-amd64', 'publish-arm64',
                 'sign-bins-amd64', 'sign-bins-arm64',
                 'msi-amd64', 'msi-arm64', 'msi-all',
                 'sign', 'all')]
    [string]$Target = 'build',

    [string]$Configuration = 'Release',

    [string]$VersionPrefix,

    [string]$VertexCertPfx     = $env:VERTEX_CERT_PFX,
    [string]$VertexCertPwd     = $env:VERTEX_CERT_PWD,
    [string]$VertexTimestamper = 'http://timestamp.digicert.com'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $PSCommandPath
$root = Split-Path -Parent $here
Set-Location $root

function Invoke-Dotnet {
    param([Parameter(Mandatory)][string[]]$Args)
    Write-Host "→ dotnet $($Args -join ' ')" -ForegroundColor Cyan
    & dotnet @Args
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Args -join ' ') failed (exit $LASTEXITCODE)" }
}

function Build-Args {
    $a = @('--configuration', $Configuration, '-nologo')
    if ($VersionPrefix) { $a += "-p:VersionPrefix=$VersionPrefix" }
    return $a
}

function Find-Signtool {
    # Pick the newest installed Windows SDK signtool by parsing the SDK
    # version directory (...\bin\<10.0.X>\x64\signtool.exe) as [Version].
    # Plain Sort-Object on FileInfo would lex-sort '10.0.9999.0' above
    # '10.0.22621.0' which is wrong. Falls back to PATH if no SDK found.
    $sdkBin = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe' -ErrorAction SilentlyContinue
    $best   = $sdkBin | Sort-Object -Descending -Property @{Expression = {
        try { [System.Version]::Parse($_.Directory.Parent.Name) }
        catch { [System.Version]::new(0,0) }
    }} | Select-Object -First 1
    if ($best) { return $best.FullName }
    $cmd = (Get-Command signtool -ErrorAction SilentlyContinue).Source
    if ($cmd) { return [string]$cmd }
    throw 'signtool.exe not found - install Windows 10/11 SDK or expose it on PATH.'
}

function Invoke-Signtool {
    param(
        [Parameter(Mandatory)][string[]]$Files,
        [Parameter(Mandatory)][string]$Description
    )
    if (-not $VertexCertPfx) {
        Write-Host "⊘ Skipping signtool — VERTEX_CERT_PFX not set." -ForegroundColor Yellow
        return
    }
    if (-not (Test-Path $VertexCertPfx)) {
        throw "VertexCertPfx not found: $VertexCertPfx"
    }
    $signtool = Find-Signtool
    # NB: not '$args' — that's an automatic variable in PS 5.1 and
    # writing to it confuses the parser.
    $signArgs = @(
        'sign', '/fd', 'SHA256',
        '/tr', $VertexTimestamper, '/td', 'SHA256',
        '/f', $VertexCertPfx, '/p', $VertexCertPwd,
        '/d', $Description, '/du', 'https://vertices.ru'
    ) + $Files
    Write-Host "→ signtool sign /fd SHA256 /tr $VertexTimestamper /td SHA256 /f *** /p *** /d ""$Description"" $($Files -join ' ')" -ForegroundColor Cyan
    & $signtool @signArgs
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit $LASTEXITCODE)" }
}

function Publish-Vertex {
    param([Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Rid)

    $out = Join-Path $root "publish\$Rid"
    Invoke-Dotnet (@('publish', 'src\Vertex.Service\Vertex.Service.csproj',
                     '--runtime', $Rid, '--self-contained', 'true',
                     '-p:PublishSingleFile=true', '-o', "$out\Service") + (Build-Args))
    Invoke-Dotnet (@('publish', 'src\Vertex.App\Vertex.App.csproj',
                     '--runtime', $Rid, '--self-contained', 'true',
                     '-o', "$out\App") + (Build-Args))
}

function Sign-Bins {
    param([Parameter(Mandatory)][ValidateSet('win-x64','win-arm64')][string]$Rid)

    $out = Join-Path $root "publish\$Rid"
    $files = @(
        (Join-Path $out 'Service\Vertex.Service.exe'),
        (Join-Path $out 'App\Vertex.App.exe')
    ) | Where-Object { Test-Path $_ }
    if ($files.Count -eq 0) {
        throw "No bins to sign in $out — run publish-* first."
    }
    Invoke-Signtool -Files $files -Description 'Vertex VPN'
}

function Build-Msi {
    param([Parameter(Mandatory)][ValidateSet('x64','ARM64')][string]$Platform)

    # WiX project consumes publish/<rid>/{Service,App}/* (see wixproj
    # DefineConstants). Caller must run publish-<arch> beforehand.
    $msbuildArgs = @(
        'build', 'packaging\Vertex.Setup.wixproj',
        '-c', $Configuration,
        "-p:Platform=$Platform",
        '-nologo'
    )
    if ($VersionPrefix)   { $msbuildArgs += "-p:VersionPrefix=$VersionPrefix" }
    if ($VertexCertPfx)   { $msbuildArgs += "-p:VertexCertPfx=$VertexCertPfx" }
    if ($VertexCertPwd)   { $msbuildArgs += "-p:VertexCertPwd=$VertexCertPwd" }
    if ($VertexTimestamper){$msbuildArgs += "-p:VertexTimestamper=$VertexTimestamper"}
    Invoke-Dotnet $msbuildArgs

    $msi = Get-ChildItem (Join-Path $root "packaging\bin\$Platform\$Configuration\*\*.msi") -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($msi) {
        Write-Host "MSI: $($msi.FullName)" -ForegroundColor Green
    }

    # CopyToDist target in the wixproj also publishes to ../../dist/windows/.
    $repoRoot = Split-Path -Parent (Split-Path -Parent $root)  # vertex/clients/windows -> vertex
    $distMsi = Get-ChildItem (Join-Path $repoRoot 'dist\windows\*.msi') -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if ($distMsi) {
        Write-Host "Dist: $($distMsi.FullName)" -ForegroundColor Green
    }
}

switch ($Target) {
    'restore' {
        Invoke-Dotnet @('restore', 'Vertex.sln')
    }
    'build-debug' {
        $script:Configuration = 'Debug'
        Invoke-Dotnet (@('build', 'Vertex.sln') + (Build-Args))
    }
    'build' {
        Invoke-Dotnet (@('build', 'Vertex.sln') + (Build-Args))
    }
    'test' {
        # Test projects run separately because each lives in its own
        # output directory and `dotnet test <sln>` doesn't propagate
        # ProjectReference outputs across them.
        Invoke-Dotnet (@('test', 'src\Vertex.Core.Tests\Vertex.Core.Tests.csproj')       + (Build-Args))
        Invoke-Dotnet (@('test', 'src\Vertex.Service.Tests\Vertex.Service.Tests.csproj') + (Build-Args))
    }
    'clean' {
        Invoke-Dotnet @('clean', 'Vertex.sln', '-nologo')
        Get-ChildItem -Recurse -Directory -Force -Include bin,obj |
            Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $root 'publish')
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $root 'packaging\bin')
        Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $root 'packaging\obj')
    }
    'publish-amd64'   { Publish-Vertex -Rid 'win-x64' }
    'publish-arm64'   { Publish-Vertex -Rid 'win-arm64' }
    'sign-bins-amd64' { Sign-Bins -Rid 'win-x64' }
    'sign-bins-arm64' { Sign-Bins -Rid 'win-arm64' }
    'msi-amd64' {
        # Full chain: publish → sign exes → build + sign MSI.
        Publish-Vertex -Rid 'win-x64'
        Sign-Bins      -Rid 'win-x64'
        Build-Msi      -Platform 'x64'
    }
    'msi-arm64' {
        Publish-Vertex -Rid 'win-arm64'
        Sign-Bins      -Rid 'win-arm64'
        Build-Msi      -Platform 'ARM64'
    }
    'msi-all' {
        & $PSCommandPath -Target msi-amd64 @PSBoundParameters
        & $PSCommandPath -Target msi-arm64 @PSBoundParameters
    }
    'sign' {
        Write-Warning 'Use sign-bins-amd64 / sign-bins-arm64 (or msi-* which signs end-to-end).'
    }
    'all' {
        & $PSCommandPath -Target restore @PSBoundParameters
        & $PSCommandPath -Target build   @PSBoundParameters
        & $PSCommandPath -Target test    @PSBoundParameters
    }
}
