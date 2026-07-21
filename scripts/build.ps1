[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
dotnet build (Join-Path $root 'NextRdp.slnx') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
