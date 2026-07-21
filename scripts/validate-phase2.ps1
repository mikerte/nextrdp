[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$catalogPath = Join-Path $projectRoot 'docs\reference\feature-catalog.csv'
$matrixPath = Join-Path $projectRoot 'docs\validation\phase2-completion.md'
$targetAcceptancePath = Join-Path $projectRoot 'docs\phase2-target-acceptance.md'

& (Join-Path $PSScriptRoot 'validate-feature-catalog.ps1')

$rows = @(Import-Csv -LiteralPath $catalogPath -Encoding utf8)
$expected = @($rows | Where-Object { $_.phase -eq '2' -and $_.item_type -eq 'software_feature' } | ForEach-Object id | Sort-Object)
$matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding utf8
$matches = @([regex]::Matches($matrix, 'NX-L\d{4}') | ForEach-Object Value)
$actual = @($matches | Sort-Object)

if ($expected.Count -ne 105) {
    throw "The reconciled Phase 2 catalog must contain exactly 105 software requirements; found $($expected.Count)."
}

$duplicates = @($matches | Group-Object | Where-Object Count -ne 1)
if ($duplicates.Count -gt 0) {
    throw "The Phase 2 matrix contains duplicate catalog IDs: $($duplicates.Name -join ', ')."
}

$comparison = @(Compare-Object -ReferenceObject $expected -DifferenceObject $actual)
if ($comparison.Count -gt 0) {
    $details = $comparison | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    throw "The Phase 2 matrix does not map the catalog exactly:`n$($details -join [Environment]::NewLine)"
}

$expectedTargetAcceptance = @($rows |
    Where-Object { $_.phase -eq '2' -and $_.item_type -eq 'software_feature' -and $_.status -eq 'implemented' } |
    ForEach-Object id |
    Sort-Object)
$targetAcceptance = Get-Content -LiteralPath $targetAcceptancePath -Raw -Encoding utf8
$targetMatches = @([regex]::Matches($targetAcceptance, 'NX-L\d{4}') | ForEach-Object Value)
$targetActual = @($targetMatches | Sort-Object)

if ($expectedTargetAcceptance.Count -ne 28) {
    throw "The Phase 2 catalog must currently contain exactly 28 target-dependent implemented requirements; found $($expectedTargetAcceptance.Count)."
}

$targetDuplicates = @($targetMatches | Group-Object | Where-Object Count -ne 1)
if ($targetDuplicates.Count -gt 0) {
    throw "The Phase 2 target-acceptance checklist contains duplicate catalog IDs: $($targetDuplicates.Name -join ', ')."
}

$targetComparison = @(Compare-Object -ReferenceObject $expectedTargetAcceptance -DifferenceObject $targetActual)
if ($targetComparison.Count -gt 0) {
    $targetDetails = $targetComparison | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" }
    throw "The Phase 2 target-acceptance checklist does not map the implemented catalog rows exactly:`n$($targetDetails -join [Environment]::NewLine)"
}

Write-Host "Phase 2 scope valid: $($expected.Count) software requirements mapped exactly once; $($expectedTargetAcceptance.Count) target-dependent requirements mapped exactly once."
