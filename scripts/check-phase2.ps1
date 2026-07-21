[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$Package,
    [switch]$Live,
    [string]$BaseUri = 'http://127.0.0.1:5090',
    [string]$PostgresPassword = '',
    [string]$BackupPassphrase = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$adminRoot = Join-Path $root 'src\NextRdp.Admin'

foreach ($scriptFile in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.ps1' -File) {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($scriptFile.FullName, [ref]$tokens, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) {
        throw "PowerShell parse failure in $($scriptFile.Name): $($parseErrors[0].Message)"
    }
}

& (Join-Path $PSScriptRoot 'validate-phase2.ps1')
& (Join-Path $PSScriptRoot 'record-phase2-acceptance.ps1') -ValidateOnly
& (Join-Path $PSScriptRoot 'test-phase2-acceptance-recorder.ps1')

$catalog = @(Import-Csv -LiteralPath (Join-Path $root 'docs\reference\feature-catalog.csv') -Encoding utf8)
$requirements = @($catalog | Where-Object { $_.phase -eq '2' -and $_.item_type -eq 'software_feature' })
$open = @($requirements | Where-Object { $_.status -notin @('implemented', 'verified') })
if ($open.Count -gt 0) { throw "Phase 2 catalog still contains $($open.Count) open software requirement(s)." }
$missingEvidence = @($requirements | Where-Object { [string]::IsNullOrWhiteSpace($_.evidence) -or $_.evidence -eq 'pending' })
if ($missingEvidence.Count -gt 0) { throw "Phase 2 catalog still contains $($missingEvidence.Count) requirement(s) without direct evidence." }
$genericAcceptance = 'Automated or documented validation demonstrates the capability and records evidence.'
$weakAcceptance = @($requirements | Where-Object { [string]::IsNullOrWhiteSpace($_.acceptance) -or $_.acceptance -eq $genericAcceptance })
if ($weakAcceptance.Count -gt 0) { throw "Phase 2 catalog still contains $($weakAcceptance.Count) missing or generic acceptance criterion/criteria." }

$matrixPath = Join-Path $root 'docs\validation\phase2-completion.md'
$matrix = Get-Content -LiteralPath $matrixPath -Raw -Encoding utf8
if ($matrix -match '(?im)^\|[^\r\n]*\|\s*(?:Pending|Planned)(?:\s*/\s*(?:Pending|Planned))?\s*\|\s*$') {
    throw 'The Phase 2 completion matrix still contains a Pending or Planned evidence row.'
}

& (Join-Path $PSScriptRoot 'check-phase1.ps1') -Configuration $Configuration -Package:$Package

Push-Location $adminRoot
try {
    & npm.cmd run check
    if ($LASTEXITCODE -ne 0) { throw "Administration-console checks failed with exit code $LASTEXITCODE." }
    & npm.cmd audit --audit-level=low
    if ($LASTEXITCODE -ne 0) { throw "Administration-console dependency audit failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}

$textExtensions = @('.cs', '.xaml', '.json', '.xml', '.ps1', '.csproj', '.props', '.targets', '.md', '.csv', '.txt', '.yml', '.yaml', '.ts', '.tsx', '.js', '.mjs', '.sql', '.toml')
$excludedDirectories = '[\\/](\.git|\.vs|artifacts|bin|obj|TestResults|packages|node_modules|dist|coverage)[\\/]'
$candidateFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File |
    Where-Object { $_.Extension -in $textExtensions -and $_.FullName -notmatch $excludedDirectories -and $_.FullName -notmatch '[\\/]scripts[\\/]check-phase\d+\.ps1$' })
$credentialChecks = @(
    @{ Name = 'credential assignment'; Pattern = '(?i)(password|passwd|pwd|access[_-]?token|client[_-]?secret)\s*[:=]\s*["''][^"'']{8,}["'']' },
    @{ Name = 'private key'; Pattern = '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' },
    @{ Name = 'credentialed URI'; Pattern = '(?i)\b(?:https?|postgres(?:ql)?|mssql|sqlserver)://[^/\s:@]+:[^@\s/]{8,}@' },
    @{ Name = 'RDP password blob'; Pattern = '(?i)^\s*password\s+51\s*:\s*b\s*:\s*[0-9a-f]{16,}\s*$' },
    @{ Name = 'known access-token prefix'; Pattern = '(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9_-]{20,})\b' }
)
foreach ($check in $credentialChecks) {
    $matches = @($candidateFiles | Select-String -Pattern $check.Pattern)
    if ($check.Name -eq 'credentialed URI') {
        $matches = @($matches | Where-Object { $_.Line -notmatch '\$\{[A-Z][A-Z0-9_]*(?::[^}]*)?\}' })
    }
    if ($matches.Count -gt 0) { throw "Potential $($check.Name) found in project text: $($matches[0].Path):$($matches[0].LineNumber)." }
}

$previousComposePassword = $env:POSTGRES_PASSWORD
try {
    $composeRandom = New-Object byte[] 24
    $composeGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $composeGenerator.GetBytes($composeRandom) } finally { $composeGenerator.Dispose() }
    $env:POSTGRES_PASSWORD = [Convert]::ToBase64String($composeRandom)
    & docker compose config --quiet
    if ($LASTEXITCODE -ne 0) { throw "Docker Compose configuration validation failed with exit code $LASTEXITCODE." }
}
finally {
    $env:POSTGRES_PASSWORD = $previousComposePassword
}

if ($Live) {
    if ([string]::IsNullOrWhiteSpace($PostgresPassword)) { throw 'PostgresPassword is required for live Phase 2 validation.' }
    if ([string]::IsNullOrWhiteSpace($BackupPassphrase)) {
        $random = New-Object byte[] 32
        $generator = [Security.Cryptography.RandomNumberGenerator]::Create()
        try { $generator.GetBytes($random) } finally { $generator.Dispose() }
        $BackupPassphrase = [Convert]::ToBase64String($random)
    }

    $previousLivePassword = $env:POSTGRES_PASSWORD
    try {
        $env:POSTGRES_PASSWORD = $PostgresPassword
        & docker compose up -d --build --wait
        if ($LASTEXITCODE -ne 0) { throw "Docker Compose startup failed with exit code $LASTEXITCODE." }
        & (Join-Path $PSScriptRoot 'smoke-phase2.ps1') -BaseUri $BaseUri -BackupPassphrase $BackupPassphrase
        & (Join-Path $PSScriptRoot 'smoke-phase2-automation.ps1') -BaseUri $BaseUri
        & (Join-Path $PSScriptRoot 'test-phase2-restore.ps1') -SourceBaseUri $BaseUri -PostgresPassword $PostgresPassword -BackupPassphrase $BackupPassphrase
    }
    finally {
        $env:POSTGRES_PASSWORD = $previousLivePassword
    }
}

$liveSummary = if ($Live) { ', live control-plane/automation smoke, and clean encrypted restore drill' } else { '' }
Write-Host "Phase 2 checks passed: exact $($requirements.Count)-requirement mapping and evidence, Phase 1 regression suite, administration-console checks, dependency audits, $($candidateFiles.Count)-file credential scan, Compose validation$liveSummary."
