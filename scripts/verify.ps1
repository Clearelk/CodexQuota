<#
.SYNOPSIS
  Runs the full verification pipeline: build, tests, format check, and publish.

.DESCRIPTION
  Idempotent and non-destructive. Use -SkipPublish for a fast inner loop. All output
  stays inside the workspace. Fails fast on the first non-zero exit code.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$logDirectory = Join-Path $root 'artifacts/verification'
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
Start-Transcript -Path (Join-Path $logDirectory 'build-test-publish.log') -Force | Out-Null
Push-Location $root
try {
    Write-Host '=== dotnet build ==='
    dotnet build CodexQuota.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

    Write-Host '=== dotnet test ==='
    dotnet test CodexQuota.slnx -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed with exit code $LASTEXITCODE" }

    Write-Host '=== dotnet format (verify) ==='
    dotnet format CodexQuota.slnx --verify-no-changes --no-restore
    if ($LASTEXITCODE -ne 0) { throw "format check failed with exit code $LASTEXITCODE" }

    if (-not $SkipPublish) {
        Write-Host '=== publish ==='
        & (Join-Path $PSScriptRoot 'publish.ps1') -Configuration $Configuration
    }

    Write-Host 'verify: OK'
}
finally {
    Pop-Location
    Stop-Transcript | Out-Null
}
