<#
.SYNOPSIS
Builds the Graphics Dev Toolbar VSIX.

.EXAMPLE
./scripts/package.ps1
#>
param(
    [string]$Configuration = 'Release',

    [string]$OutputDir = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$solution = Join-Path $repoRoot 'GraphicsDevToolbar.sln'
$projectDir = Join-Path $repoRoot 'src'

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe not found at '$vswhere'. Install Visual Studio or Build Tools with the MSBuild component."
}
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found. Install Visual Studio or Build Tools with the MSBuild component.'
}

Write-Host "Packaging with $msbuild"
& $msbuild $solution /restore /t:Rebuild /m /v:minimal /nologo "/p:Configuration=$Configuration"
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE."
}

$vsix = Join-Path $projectDir "bin\$Configuration\GraphicsDevToolbar.vsix"
if (-not (Test-Path $vsix)) {
    throw "Build succeeded but '$vsix' was not produced."
}

New-Item -ItemType Directory -Force $OutputDir | Out-Null
$destination = Join-Path (Resolve-Path $OutputDir) 'GraphicsDevToolbar.vsix'
Copy-Item $vsix $destination -Force
Write-Host "Package: $destination"
