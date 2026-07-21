[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Package
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

foreach ($scriptFile in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        throw "PowerShell parse failure in $($scriptFile.Name): $($parseErrors[0].Message)"
    }
}

. (Join-Path $PSScriptRoot 'phase1-platform.ps1')
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType 1 -BuildNumber 22000 -Is64BitOperatingSystem $true)) {
    throw 'The Phase 1 platform predicate rejected the minimum supported Windows 11 x64 workstation.'
}
if (Test-NextRdpPhaseOneClientPlatform -ProductType 1 -BuildNumber 19045 -Is64BitOperatingSystem $true) {
    throw 'The Phase 1 platform predicate accepted a Windows 10 workstation.'
}
if (Test-NextRdpPhaseOneClientPlatform -ProductType 3 -BuildNumber 26100 -Is64BitOperatingSystem $true) {
    throw 'The Phase 1 platform predicate accepted a Windows Server installation.'
}
if (Test-NextRdpPhaseOneClientPlatform -ProductType 1 -BuildNumber 26100 -Is64BitOperatingSystem $false) {
    throw 'The Phase 1 platform predicate accepted a non-x64 Windows workstation.'
}

& (Join-Path $PSScriptRoot 'validate-feature-catalog.ps1')
& (Join-Path $PSScriptRoot 'record-phase1-acceptance.ps1') -ValidateOnly
$catalog = Import-Csv (Join-Path $root 'docs\reference\feature-catalog.csv')
$openPhaseOne = @($catalog | Where-Object { $_.phase -eq '1' -and $_.item_type -eq 'software_feature' -and $_.status -notin @('implemented', 'verified') })
if ($openPhaseOne.Count -gt 0) { throw "Phase 1 catalog still contains $($openPhaseOne.Count) open software requirement(s)." }
$missingEvidence = @($catalog | Where-Object { $_.phase -eq '1' -and $_.item_type -eq 'software_feature' -and ($_.evidence -eq 'pending' -or [string]::IsNullOrWhiteSpace($_.evidence)) })
if ($missingEvidence.Count -gt 0) { throw "Phase 1 catalog still contains $($missingEvidence.Count) requirement(s) without evidence." }
$phaseOneRequirements = @($catalog | Where-Object { $_.phase -eq '1' -and $_.item_type -eq 'software_feature' })
$genericAcceptance = 'Automated or documented validation demonstrates the capability and records evidence.'
$weakAcceptance = @($phaseOneRequirements | Where-Object {
    [string]::IsNullOrWhiteSpace($_.acceptance) -or $_.acceptance -eq $genericAcceptance
})
if ($weakAcceptance.Count -gt 0) { throw "Phase 1 catalog still contains $($weakAcceptance.Count) missing or generic acceptance criterion/criteria." }
$duplicateAcceptance = @($phaseOneRequirements | Group-Object acceptance | Where-Object Count -gt 1)
if ($duplicateAcceptance.Count -gt 0) { throw 'Phase 1 catalog contains duplicated acceptance criteria; each requirement needs its own testable criterion.' }
$completionMatrixPath = Join-Path $root 'docs\validation\phase1-completion.md'
$completionMatrix = Get-Content -LiteralPath $completionMatrixPath -Raw
foreach ($requirement in $phaseOneRequirements)
{
    $occurrences = [regex]::Matches($completionMatrix, "(?<![A-Z0-9-])$([regex]::Escape($requirement.id))(?![A-Z0-9-])").Count
    if ($occurrences -ne 1) { throw "Phase 1 completion matrix must contain $($requirement.id) exactly once; found $occurrences occurrence(s)." }
}
$matrixIds = @([regex]::Matches($completionMatrix, '(?<![A-Z0-9-])NX-L\d{4}(?![A-Z0-9-])') | ForEach-Object Value | Sort-Object -Unique)
$phaseOneIds = @($phaseOneRequirements.id | Sort-Object -Unique)
$unexpectedMatrixIds = @($matrixIds | Where-Object { $_ -notin $phaseOneIds })
if ($unexpectedMatrixIds.Count -gt 0) { throw "Phase 1 completion matrix contains a non-Phase-1 catalog ID: $($unexpectedMatrixIds[0])." }

$sampleInventoryPath = Join-Path $root 'samples\sample-inventory.json'
$sampleInventoryRaw = Get-Content -LiteralPath $sampleInventoryPath -Raw
$sampleInventory = $sampleInventoryRaw | ConvertFrom-Json
if ($sampleInventory.schemaVersion -ne 1 -or @($sampleInventory.connections).Count -lt 1) {
    throw 'The secret-free sample inventory is missing a supported schema version or example connection.'
}
if ($sampleInventoryRaw -match '(?i)"(?:password|passwd|pwd|access[_-]?token|client[_-]?secret|credential(?:Reference)?)"\s*:') {
    throw 'The sample inventory contains a credential-bearing property.'
}

dotnet build (Join-Path $root 'NextRdp.slnx') --configuration $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
dotnet format (Join-Path $root 'NextRdp.slnx') --verify-no-changes --no-restore --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw "Formatting verification failed with exit code $LASTEXITCODE." }
dotnet test (Join-Path $root 'NextRdp.slnx') --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
dotnet list (Join-Path $root 'NextRdp.slnx') package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw "Dependency vulnerability review failed with exit code $LASTEXITCODE." }

$textExtensions = @('.cs', '.xaml', '.json', '.rdp', '.rdg', '.xml', '.ps1', '.csproj', '.props', '.targets', '.md', '.csv', '.txt', '.yml', '.yaml')
$excludedDirectories = '[\\/](\.git|\.vs|artifacts|bin|obj|TestResults|packages|node_modules|dist|coverage)[\\/]'
$candidateFiles = Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Extension -in $textExtensions -and $_.FullName -notmatch $excludedDirectories -and $_.FullName -notmatch '[\\/]scripts[\\/]check-phase\d+\.ps1$' }
$credentialChecks = @(
    @{ Name = 'credential assignment'; Pattern = '(?i)(password|passwd|pwd|access[_-]?token|client[_-]?secret)\s*[:=]\s*["''][^"'']{8,}["'']' },
    @{ Name = 'private key'; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' },
    @{ Name = 'credentialed URI'; Pattern = '(?i)\b(?:https?|postgres(?:ql)?|mssql|sqlserver)://[^/\s:@]+:[^@\s/]{8,}@' },
    @{ Name = 'RDP password blob'; Pattern = '(?i)^\s*password\s+51\s*:\s*b\s*:\s*[0-9a-f]{16,}\s*$' },
    @{ Name = 'known access-token prefix'; Pattern = '(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9_-]{20,})\b' }
)
foreach ($check in $credentialChecks)
{
    $matches = @($candidateFiles | Select-String -Pattern $check.Pattern)
    if ($check.Name -eq 'credentialed URI') {
        $matches = @($matches | Where-Object { $_.Line -notmatch '\$\{[A-Z][A-Z0-9_]*(?::[^}]*)?\}' })
    }
    if ($matches.Count -gt 0) { throw "Potential $($check.Name) found in project text: $($matches[0].Path):$($matches[0].LineNumber)." }
}

if ($Package) {
    & (Join-Path $PSScriptRoot 'publish-windows.ps1') -Configuration $Configuration
    & (Join-Path $PSScriptRoot 'smoke-unpackaged.ps1')
    & (Join-Path $PSScriptRoot 'package-msix.ps1')
    & (Join-Path $PSScriptRoot 'test-msix-install.ps1') -PreflightOnly
    & (Join-Path $PSScriptRoot 'test-windows-certification.ps1') -PreflightOnly
}

Write-Host "Phase 1 checks passed: catalog, exact $($phaseOneRequirements.Count)-requirement completion mapping, build, tests, dependency review, $($candidateFiles.Count)-file multi-pattern project credential scan, and requested package checks."
