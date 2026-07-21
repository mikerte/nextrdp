[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sourcePath = Join-Path $projectRoot 'docs\reference\enterprise-feature-baseline.md'
$catalogPath = Join-Path $projectRoot 'docs\reference\feature-catalog.csv'

$sourceLines = Get-Content -LiteralPath $sourcePath -Encoding utf8
$expectedLines = @($sourceLines | ForEach-Object -Begin { $line = 0 } -Process {
    $line++
    if (-not [string]::IsNullOrWhiteSpace($_)) { $line }
})
$rows = @(Import-Csv -LiteralPath $catalogPath -Encoding utf8)

if ($rows.Count -ne $expectedLines.Count) {
    throw "Catalog row count $($rows.Count) does not match non-empty source-line count $($expectedLines.Count)."
}

$duplicateIds = @($rows | Group-Object id | Where-Object Count -ne 1)
$duplicateLines = @($rows | Group-Object source_line | Where-Object Count -ne 1)
if ($duplicateIds.Count -gt 0) { throw "Feature catalog contains duplicate IDs." }
if ($duplicateLines.Count -gt 0) { throw "Feature catalog contains duplicate source-line mappings." }

$mappedLines = @($rows.source_line | ForEach-Object { [int]$_ } | Sort-Object)
if (Compare-Object $expectedLines $mappedLines) { throw "Feature catalog does not map every non-empty source line exactly once." }

$validPhases = @('1','2','3','4','5','6','reference')
$invalid = @($rows | Where-Object {
    $_.phase -notin $validPhases -or
    [string]::IsNullOrWhiteSpace($_.requirement) -or
    [string]::IsNullOrWhiteSpace($_.acceptance) -or
    [string]::IsNullOrWhiteSpace($_.evidence)
})
if ($invalid.Count -gt 0) { throw "Feature catalog contains invalid phase values or empty requirement, acceptance, or evidence fields." }

Write-Host "Feature catalog valid: $($rows.Count) traceable entries."
