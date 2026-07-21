[CmdletBinding()]
param(
    [string]$ResultsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'samples\phase1-acceptance-results.template.json'),
    [string]$PackagePath = '',
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\acceptance'),
    [string]$InstallEvidencePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\acceptance\msix-install-smoke.json'),
    [string]$CertificationEvidencePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\certification\NextRDP-WACK-evidence.json'),
    [switch]$ValidateOnly,
    [switch]$AllowIncomplete
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
$release = Get-NextRdpReleaseMetadata
if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path (Split-Path -Parent $PSScriptRoot) "artifacts\msix\NextRDP-$($release.MsixVersion)-x64.msix"
}
. (Join-Path $PSScriptRoot 'phase1-platform.ps1')

function ConvertTo-EvidenceTimestamp([object]$Value, [string]$Label) {
    $parsed = [DateTimeOffset]::MinValue
    $styles = [Globalization.DateTimeStyles]::None
    if (-not [DateTimeOffset]::TryParse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) {
        throw "$Label is not a valid round-trip timestamp."
    }
    if ($parsed -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw "$Label is unexpectedly in the future."
    }
    return $parsed
}

function Assert-PhaseOnePlatformEvidence([object]$Platform, [string]$Label) {
    if ($null -eq $Platform -or
        -not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$Platform.productType) -BuildNumber ([int]$Platform.build) -Is64BitOperatingSystem ([string]$Platform.architecture -eq 'x64'))) {
        throw "$Label does not identify a supported Windows 11 x64 client workstation."
    }
}

$definitions = @(
    [pscustomobject]@{ Id = 'clean-msix-install'; Label = 'Install the signed MSIX on a clean elevated Windows 11 x64 test user'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'create-or-import'; Label = 'Create a profile or import a safe .rdp/.rdg file through preview'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'search'; Label = 'Find the profile with global search'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'prompt-only-authentication'; Label = 'Authenticate through the Windows credential prompt without NextRDP storing a password'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'embedded-tabs'; Label = 'Use an authenticated embedded session and multiple tabs'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'sizing-and-live-resize'; Label = 'Confirm initial desktop sizing, smart sizing, DPI behavior, and live resize'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'full-screen-exit'; Label = 'Enter and leave full screen through the connection bar or Ctrl+Alt+Break'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'clipboard-redirection'; Label = 'Verify configured clipboard redirection'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'drive-redirection'; Label = 'Verify configured drive redirection'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'printer-redirection'; Label = 'Verify configured printer redirection'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'audio-redirection'; Label = 'Verify configured audio playback redirection'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'automatic-reconnect'; Label = 'Interrupt and restore network reachability and verify bounded automatic reconnect'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'clean-disconnect'; Label = 'Disconnect and close tabs without a crash or a false live-session recovery record'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'restart-persistence'; Label = 'Restart the app and retain inventory, settings, and metadata-only history'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'password-absence'; Label = 'Verify database, exports, logs, and support artifacts contain no password'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'recent-history'; Label = 'Verify the successful connection appears in Recent with the updated timestamp'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'production-safety'; Label = 'Verify blocking Production warning, textual label, session border, and optional watermark'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'screen-reader'; Label = 'Complete the core workflow with a Windows screen reader and meaningful announcements'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'touch-and-pen'; Label = 'Check touch and pen behavior on suitable hardware'; Required = $false; AllowsNotApplicable = $true },
    [pscustomobject]@{ Id = 'windows-app-certification'; Label = 'Run the Windows App Certification Kit and retain a passing XML report'; Required = $true; AllowsNotApplicable = $false },
    [pscustomobject]@{ Id = 'clean-uninstall'; Label = 'Uninstall the package and verify temporary trust and packaged data cleanup'; Required = $true; AllowsNotApplicable = $false }
)

if (-not (Test-Path -LiteralPath $ResultsPath -PathType Leaf)) {
    throw "Phase 1 acceptance results file was not found: $ResultsPath"
}

$inputDocument = Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json
if ($inputDocument.schemaVersion -ne 1) {
    throw 'Phase 1 acceptance results must use schemaVersion 1.'
}

$inputChecks = @($inputDocument.checks)
if ($inputChecks.Count -ne $definitions.Count) {
    throw "Phase 1 acceptance results must contain exactly $($definitions.Count) checks; found $($inputChecks.Count)."
}

$duplicateIds = @($inputChecks | Group-Object id | Where-Object Count -ne 1)
if ($duplicateIds.Count -gt 0) {
    throw "Phase 1 acceptance results contain a duplicate check ID: $($duplicateIds[0].Name)."
}

$unknownIds = @($inputChecks | Where-Object { $_.id -notin $definitions.Id })
if ($unknownIds.Count -gt 0) {
    throw "Phase 1 acceptance results contain an unknown check ID: $($unknownIds[0].id)."
}

$allowedResults = @('pass', 'fail', 'not-run', 'not-applicable')
$recordedChecks = foreach ($definition in $definitions) {
    $matches = @($inputChecks | Where-Object id -eq $definition.Id)
    if ($matches.Count -ne 1) {
        throw "Phase 1 acceptance results must contain $($definition.Id) exactly once; found $($matches.Count)."
    }

    $result = ([string]$matches[0].result).Trim().ToLowerInvariant()
    if ($result -notin $allowedResults) {
        throw "Check $($definition.Id) has invalid result '$result'. Allowed values: $($allowedResults -join ', ')."
    }
    if ($result -eq 'not-applicable' -and -not $definition.AllowsNotApplicable) {
        throw "Check $($definition.Id) is required and cannot be marked not-applicable."
    }

    [ordered]@{
        id = $definition.Id
        label = $definition.Label
        required = $definition.Required
        result = $result
    }
}

$incompleteChecks = @($recordedChecks | Where-Object {
    ($_.required -and $_.result -ne 'pass') -or
    (-not $_.required -and $_.result -notin @('pass', 'not-applicable'))
})
$overallResult = if ($incompleteChecks.Count -eq 0) { 'pass' } else { 'incomplete' }

if ($ValidateOnly) {
    Write-Host "Phase 1 acceptance input schema valid: $($recordedChecks.Count) checks; current result $overallResult."
    return
}

$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$operatingSystem.ProductType) -BuildNumber ([int]$operatingSystem.BuildNumber) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem))) {
    throw 'Phase 1 acceptance evidence requires a Windows 11 x64 client workstation (build 22000 or newer); Windows Server does not satisfy the Phase 1 release target.'
}
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
    throw "The Phase 1 MSIX package was not found: $PackagePath"
}

$signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
    throw "The Phase 1 MSIX signature is not valid: $($signature.Status)."
}

$package = Get-Item -LiteralPath $PackagePath
$packageHash = Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
$installEvidenceSummary = $null
$installResultIds = @('clean-msix-install', 'clean-uninstall')
$installPassClaimed = @($recordedChecks | Where-Object { $_.id -in $installResultIds -and $_.result -eq 'pass' }).Count -gt 0
if ($installPassClaimed) {
    if (-not (Test-Path -LiteralPath $InstallEvidencePath -PathType Leaf)) {
        throw "Clean install/uninstall was marked pass, but its machine-generated evidence file was not found: $InstallEvidencePath"
    }
    $installEvidence = Get-Content -LiteralPath $InstallEvidencePath -Raw | ConvertFrom-Json
    if ($installEvidence.schemaVersion -ne 2) { throw 'MSIX install evidence must use schemaVersion 2 with platform identity.' }
    Assert-PhaseOnePlatformEvidence $installEvidence.platform 'MSIX install evidence platform'
    $installCompletedAt = ConvertTo-EvidenceTimestamp $installEvidence.completedAtUtc 'MSIX install evidence completedAtUtc'
    if ($installEvidence.package.fileName -ne $package.Name -or $installEvidence.package.sha256 -ne $packageHash.Hash) {
        throw 'MSIX install evidence does not match the current package name and SHA-256.'
    }
    if ($installEvidence.package.signatureStatus -ne 'Valid' -or $installEvidence.package.signerThumbprint -ne $signature.SignerCertificate.Thumbprint) {
        throw 'MSIX install evidence does not match the current valid package signer.'
    }
    $requiredInstallChecks = @('install', 'launchResponsive', 'normalClose', 'uninstall', 'packagedUserDataCleanup', 'temporaryTrustCleanup')
    foreach ($checkName in $requiredInstallChecks) {
        if ($installEvidence.checks.$checkName -ne 'pass') { throw "MSIX install evidence check '$checkName' is not pass." }
    }
    $installEvidenceSummary = [ordered]@{
        fileName = [IO.Path]::GetFileName($InstallEvidencePath)
        sha256 = (Get-FileHash -LiteralPath $InstallEvidencePath -Algorithm SHA256).Hash
        packageSha256 = $installEvidence.package.sha256
        completedAtUtc = $installCompletedAt.ToString('o')
        mode = $installEvidence.mode
        manualAcceptanceDurationSeconds = $installEvidence.manualAcceptance.durationSeconds
    }
}

$installedWorkflowIds = @(
    'create-or-import', 'search', 'prompt-only-authentication', 'embedded-tabs',
    'sizing-and-live-resize', 'full-screen-exit', 'clipboard-redirection',
    'drive-redirection', 'printer-redirection', 'audio-redirection',
    'automatic-reconnect', 'clean-disconnect', 'restart-persistence',
    'password-absence', 'recent-history', 'production-safety', 'screen-reader'
)
$installedWorkflowClaimedComplete = @($recordedChecks | Where-Object {
    $_.id -in $installedWorkflowIds -and $_.result -ne 'pass'
}).Count -eq 0
if ($installedWorkflowClaimedComplete) {
    if (-not $installPassClaimed) {
        throw 'The installed workflow was marked complete without passing clean install/uninstall evidence.'
    }
    if ($installEvidence.mode -ne 'manual-acceptance' -or $installEvidence.checks.manualAcceptanceWindow -ne 'pass') {
        throw 'The installed workflow was marked complete, but install evidence does not record a manual-acceptance window.'
    }
    $manualStartedAt = ConvertTo-EvidenceTimestamp $installEvidence.manualAcceptance.startedAtUtc 'Manual acceptance startedAtUtc'
    $manualEndedAt = ConvertTo-EvidenceTimestamp $installEvidence.manualAcceptance.endedAtUtc 'Manual acceptance endedAtUtc'
    $recordedDurationSeconds = [int]$installEvidence.manualAcceptance.durationSeconds
    $calculatedDurationSeconds = [int][Math]::Round(($manualEndedAt - $manualStartedAt).TotalSeconds)
    if ($manualEndedAt -le $manualStartedAt -or $manualEndedAt -gt $installCompletedAt.AddMinutes(1)) {
        throw 'Manual acceptance timestamps are not ordered consistently with install evidence completion.'
    }
    if ([Math]::Abs($recordedDurationSeconds - $calculatedDurationSeconds) -gt 2) {
        throw 'Manual acceptance duration does not match its start and end timestamps.'
    }
    if ($recordedDurationSeconds -lt 30) {
        throw 'The installed workflow was marked complete, but the recorded manual-acceptance window was shorter than 30 seconds.'
    }
}

$certificationPassClaimed = @($recordedChecks | Where-Object { $_.id -eq 'windows-app-certification' -and $_.result -eq 'pass' }).Count -eq 1
$certificationEvidenceSummary = $null
if ($certificationPassClaimed) {
    if (-not (Test-Path -LiteralPath $CertificationEvidencePath -PathType Leaf)) {
        throw "Windows App Certification was marked pass, but its machine-generated evidence file was not found: $CertificationEvidencePath"
    }
    $certificationEvidence = Get-Content -LiteralPath $CertificationEvidencePath -Raw | ConvertFrom-Json
    if ($certificationEvidence.schemaVersion -ne 2) { throw 'Windows App Certification evidence must use schemaVersion 2 with platform identity.' }
    Assert-PhaseOnePlatformEvidence $certificationEvidence.platform 'Windows App Certification evidence platform'
    $certificationCompletedAt = ConvertTo-EvidenceTimestamp $certificationEvidence.completedAtUtc 'Windows App Certification evidence completedAtUtc'
    if ($certificationEvidence.package.fileName -ne $package.Name -or $certificationEvidence.package.sha256 -ne $packageHash.Hash) {
        throw 'Windows App Certification evidence does not match the current package name and SHA-256.'
    }
    if ($certificationEvidence.package.signatureStatus -ne 'Valid' -or $certificationEvidence.package.signerThumbprint -ne $signature.SignerCertificate.Thumbprint) {
        throw 'Windows App Certification evidence does not match the current valid package signer.'
    }
    if ($certificationEvidence.report.overallResult -ne 'pass' -or $certificationEvidence.checks.packageCleanup -ne 'pass' -or $certificationEvidence.checks.temporaryTrustCleanup -ne 'pass') {
        throw 'Windows App Certification evidence does not record a passing report and cleanup.'
    }
    $reportFileName = [IO.Path]::GetFileName([string]$certificationEvidence.report.fileName)
    if ($reportFileName -ne $certificationEvidence.report.fileName -or $reportFileName -ne 'NextRDP-WACK.xml') {
        throw 'Windows App Certification evidence contains an unexpected report filename.'
    }
    $reportPath = Join-Path (Split-Path -Parent $CertificationEvidencePath) $reportFileName
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf)) { throw "Windows App Certification XML report was not found: $reportPath" }
    $reportHash = Get-FileHash -LiteralPath $reportPath -Algorithm SHA256
    if ($reportHash.Hash -ne $certificationEvidence.report.sha256) { throw 'Windows App Certification XML hash does not match its evidence sidecar.' }
    $report = Get-Content -LiteralPath $reportPath -Raw
    if ($report -notmatch '(?i)(OVERALL_RESULT|OVERALLRESULT)[^>\r\n]*PASS|<(OVERALL_RESULT|OVERALLRESULT)>\s*PASS\s*</') {
        throw 'Windows App Certification XML does not contain a recognized passing overall result.'
    }
    $certificationEvidenceSummary = [ordered]@{
        fileName = [IO.Path]::GetFileName($CertificationEvidencePath)
        sha256 = (Get-FileHash -LiteralPath $CertificationEvidencePath -Algorithm SHA256).Hash
        reportFileName = $reportFileName
        reportSha256 = $reportHash.Hash
        packageSha256 = $certificationEvidence.package.sha256
        completedAtUtc = $certificationCompletedAt.ToString('o')
    }
}

$generatedAt = [DateTimeOffset]::UtcNow
$record = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = $generatedAt.ToString('o')
    scope = 'NextRDP Phase 1 live and clean-machine acceptance'
    privacy = 'This record intentionally excludes hostnames, IP addresses, usernames, passwords, machine names, and free-form notes.'
    platform = [ordered]@{
        product = 'Windows 11'
        version = [string]$operatingSystem.Version
        build = [int]$operatingSystem.BuildNumber
        productType = [int]$operatingSystem.ProductType
        architecture = 'x64'
    }
    package = [ordered]@{
        fileName = $package.Name
        sha256 = $packageHash.Hash
        signatureStatus = $signature.Status.ToString()
        signerThumbprint = $signature.SignerCertificate.Thumbprint
    }
    evidence = [ordered]@{
        cleanInstallAndUninstall = $installEvidenceSummary
        windowsAppCertification = $certificationEvidenceSummary
    }
    checks = $recordedChecks
    overallResult = $overallResult
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$outputPath = Join-Path $OutputDirectory "NextRDP-Phase1-Acceptance-$($generatedAt.ToString('yyyyMMdd-HHmmss'))Z.json"
$json = $record | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($outputPath, $json, [Text.UTF8Encoding]::new($false))
Write-Host "Phase 1 acceptance evidence written: $outputPath"

if ($overallResult -ne 'pass' -and -not $AllowIncomplete) {
    $ids = @($incompleteChecks.id) -join ', '
    throw "Phase 1 acceptance is incomplete. Unresolved checks: $ids."
}

if ($overallResult -eq 'pass') {
    Write-Host 'Phase 1 live and clean-machine acceptance passed.'
}
else {
    Write-Warning 'Partial acceptance evidence was recorded, but Phase 1 acceptance is not complete.'
}
