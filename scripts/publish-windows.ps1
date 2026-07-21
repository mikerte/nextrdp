[CmdletBinding()]
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts\publish\win-x64'
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedOutput = [IO.Path]::GetFullPath($output)
if (-not $resolvedOutput.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publish output must remain inside the project directory.' }
if (Test-Path -LiteralPath $resolvedOutput) { Remove-Item -LiteralPath $resolvedOutput -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null
dotnet publish (Join-Path $root 'src\NextRdp.Desktop\NextRdp.Desktop.csproj') --configuration $Configuration --runtime win-x64 --self-contained true --output $resolvedOutput -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }
foreach ($noticeFile in @('LICENSE', 'NOTICE', 'TRADEMARKS.md', 'THIRD-PARTY-NOTICES.md')) {
    Copy-Item -LiteralPath (Join-Path $root $noticeFile) -Destination (Join-Path $resolvedOutput $noticeFile)
}
$hashes = Get-ChildItem -LiteralPath $resolvedOutput -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash) *$([IO.Path]::GetFileName($_.Path))" }
$hashes | Set-Content -LiteralPath (Join-Path $resolvedOutput 'SHA256SUMS.txt') -Encoding ascii
Write-Host "Self-contained Windows x64 output: $resolvedOutput"
