<#
.SYNOPSIS
  Publishes the widget as a self-contained Windows x64 release folder.

.DESCRIPTION
  Produces artifacts/win-x64 containing CodexQuota.Desktop.exe and its runtime so the
  delivered widget does not require a separate .NET installation. The output folder is
  created if missing; no recursive delete is performed so a failed publish is
  inspectable. Run scripts/verify.ps1 for the full build/test/format verification.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = 'artifacts/win-x64'
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/CodexQuota.Desktop/CodexQuota.Desktop.csproj'
$output = Join-Path $root $OutputDirectory
$output = [IO.Path]::GetFullPath($output)
if (-not $output.StartsWith([IO.Path]::GetFullPath($root) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Publish output must stay inside the workspace.'
}

if (-not (Test-Path -LiteralPath $project)) {
    throw "project not found: $project"
}

if (-not (Test-Path -LiteralPath $output)) {
    New-Item -ItemType Directory -Path $output -Force | Out-Null
}

Write-Host "publishing $project -> $output"

dotnet publish $project -c $Configuration -r win-x64 --self-contained true -o $output --nologo
if ($LASTEXITCODE -ne 0) {
    throw "publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $output 'CodexQuota.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) {
    throw "published executable not found: $exe"
}

$sizeMiB = [math]::Round((Get-ChildItem -LiteralPath $output -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 2)
Write-Host "publish OK: $exe ($sizeMiB MiB folder)"
