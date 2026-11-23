<#
.SYNOPSIS
    Restores, builds and runs the UnderMine API test suite.

.DESCRIPTION
    Runs the full solution build and the UnderMineApi.Tests project. Exits with a non-zero
    code as soon as a command fails, so it can be used directly in CI or a pre-commit hook.

.PARAMETER Configuration
    Build configuration: Debug (default) or Release.

.PARAMETER Filter
    Optional dotnet test filter, for example "FullyQualifiedName~ClientReport".

.PARAMETER NoRestore
    Skip "dotnet restore" when the NuGet packages are already restored.

.EXAMPLE
    .\run-tests.ps1

.EXAMPLE
    .\run-tests.ps1 -Configuration Release -Filter "FullyQualifiedName~Permission"
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$Filter = '',

    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$solution = Join-Path $root 'UnderMineApi.sln'

if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution not found: $solution"
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI is not installed or not on PATH.'
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host "==> UnderMine API tests ($Configuration)" -ForegroundColor Cyan

if (-not $NoRestore) {
    Write-Host '==> dotnet restore'
    dotnet restore $solution --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host '==> dotnet build'
dotnet build $solution -c $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testArgs = @($solution, '-c', $Configuration, '--no-build', '--nologo')
if ($Filter) { $testArgs += @('--filter', $Filter) }

Write-Host '==> dotnet test'
dotnet test @testArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host 'API tests passed.' -ForegroundColor Green
