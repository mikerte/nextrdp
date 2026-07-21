[CmdletBinding()]
param(
    [string]$ResultsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'samples\phase2-acceptance-results.template.json'),
    [string]$PackagePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\msix\NextRDP-0.1.0.0-x64.msix'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\acceptance'),
    [string[]]$CorporateIdentityEvidencePath = @(),
    [string[]]$ManagedRdpEvidencePath = @(),
    [string[]]$DomainAccountEvidencePath = @(),
    [string[]]$WindowsSessionEvidencePath = @(),
    [string[]]$RedirectedDeviceEvidencePath = @(),
    [switch]$ValidateOnly,
    [switch]$AllowIncomplete
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'phase1-platform.ps1')

function Assert-ExactProperties([object]$Value, [string[]]$Expected, [string]$Label) {
    if ($null -eq $Value) { throw "$Label is missing." }
    $actual = @($Value.PSObject.Properties.Name | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $difference = @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual)
    if ($difference.Count -gt 0) {
        throw "$Label must contain only these properties: $($expectedSorted -join ', ')."
    }
}

function Get-SourceFingerprint([string]$Root) {
    $extensions = @('.cs', '.csproj', '.props', '.xaml', '.ts', '.tsx', '.json', '.yaml', '.yml', '.rego')
    $excluded = '[\\/](bin|obj|node_modules|dist|artifacts|TestResults)[\\/]'
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $_.Extension -in $extensions -and $_.FullName -notmatch $excluded -and
        ($_.FullName -match '[\\/]src[\\/]' -or $_.FullName -match '[\\/]deployment[\\/]' -or $_.Name -in @('compose.yaml', 'NextRdp.slnx', 'Directory.Build.props'))
    } | Sort-Object FullName)
    $lines = foreach ($file in $files) {
        $relative = $file.FullName.Substring($Root.Length).TrimStart([char[]]@([char]92, [char]47)).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        "$relative|$hash"
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $fingerprint = ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '') }
    finally { $sha.Dispose() }
    [ordered]@{ fileCount = $files.Count; sha256 = $fingerprint }
}

function Get-EvidenceSummaries([string[]]$Paths, [string]$GroupId) {
    if ($Paths.Count -gt 10) { throw "Evidence group $GroupId contains more than 10 artifacts." }
    $summaries = @()
    foreach ($candidate in $Paths) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { throw "Evidence group $GroupId contains an empty artifact path." }
        $resolved = (Resolve-Path -LiteralPath $candidate -ErrorAction Stop).Path
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) { throw "Evidence group $GroupId contains a path that is not a file." }
        $projectPrefix = $projectRoot.TrimEnd([char[]]@([char]92, [char]47)) + [IO.Path]::DirectorySeparatorChar
        if ($resolved.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $resolved.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Raw target evidence for $GroupId must remain outside the project tree; only its redacted hash summary may be recorded."
        }
        $file = Get-Item -LiteralPath $resolved
        if ($file.Length -le 0 -or $file.Length -gt 20MB) { throw "Evidence artifact for $GroupId must be between 1 byte and 20 MiB." }
        $format = $file.Extension.TrimStart('.').ToLowerInvariant()
        if ($format -notin @('json', 'xml', 'pdf', 'png')) { throw "Evidence artifact for $GroupId must be JSON, XML, PDF, or PNG." }
        $summaries += [ordered]@{
            ordinal = $summaries.Count + 1
            format = $format
            byteLength = $file.Length
            sha256 = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
        }
    }
    return @($summaries)
}

$groups = @(
    [pscustomobject]@{ Id = 'corporate-identity'; Label = 'Corporate OIDC, delegated MFA, and group-to-role mapping'; CheckIds = @('NX-L0152', 'NX-L0167', 'NX-L0168', 'NX-L0197') },
    [pscustomobject]@{ Id = 'managed-rdp'; Label = 'Connection Broker, RemoteApp, command line, and administrative sessions'; CheckIds = @('NX-L0015', 'NX-L0019', 'NX-L0020', 'NX-L0021', 'NX-L0083') },
    [pscustomobject]@{ Id = 'domain-account'; Label = 'Kerberos and domain account-state handling'; CheckIds = @('NX-L0032', 'NX-L0043', 'NX-L0044', 'NX-L0045') },
    [pscustomobject]@{ Id = 'windows-session'; Label = 'Windows presentation, privacy, recovery, and keyboard behavior'; CheckIds = @('NX-L0052', 'NX-L0054', 'NX-L0055', 'NX-L0064', 'NX-L0066', 'NX-L0067', 'NX-L0068', 'NX-L0069', 'NX-L0070', 'NX-L0930') },
    [pscustomobject]@{ Id = 'redirected-device'; Label = 'Redirected microphone, camera, ports, USB, and smart card'; CheckIds = @('NX-L0073', 'NX-L0074', 'NX-L0076', 'NX-L0077', 'NX-L0078') }
)
$definedIds = @($groups | ForEach-Object CheckIds | Sort-Object)
$catalog = @(Import-Csv -LiteralPath (Join-Path $projectRoot 'docs\reference\feature-catalog.csv') -Encoding utf8)
$expectedIds = @($catalog | Where-Object { $_.phase -eq '2' -and $_.item_type -eq 'software_feature' -and $_.status -eq 'implemented' } | ForEach-Object id | Sort-Object)
$definitionDifference = @(Compare-Object -ReferenceObject $expectedIds -DifferenceObject $definedIds)
if ($expectedIds.Count -ne 28 -or $definitionDifference.Count -gt 0) {
    throw 'Phase 2 acceptance definitions do not match the 28 target-dependent catalog requirements.'
}

if (-not (Test-Path -LiteralPath $ResultsPath -PathType Leaf)) { throw "Phase 2 acceptance results file was not found: $ResultsPath" }
$inputDocument = Get-Content -LiteralPath $ResultsPath -Raw | ConvertFrom-Json
Assert-ExactProperties $inputDocument @('schemaVersion', 'privacyBoundaryAcknowledged', 'promptOnlyCredentialsConfirmed', 'checks') 'Phase 2 acceptance root'
if ($inputDocument.schemaVersion -ne 1) { throw 'Phase 2 acceptance results must use schemaVersion 1.' }
if ($inputDocument.privacyBoundaryAcknowledged -isnot [bool] -or $inputDocument.promptOnlyCredentialsConfirmed -isnot [bool]) {
    throw 'Phase 2 acceptance acknowledgements must be JSON booleans.'
}

$inputChecks = @($inputDocument.checks)
if ($inputChecks.Count -ne $expectedIds.Count) { throw "Phase 2 acceptance results must contain exactly $($expectedIds.Count) checks; found $($inputChecks.Count)." }
$duplicateIds = @($inputChecks | Group-Object id | Where-Object Count -ne 1)
if ($duplicateIds.Count -gt 0) { throw "Phase 2 acceptance results contain a duplicate check ID: $($duplicateIds[0].Name)." }
$allowedResults = @('pass', 'fail', 'not-run', 'blocked')
$recordedChecks = foreach ($id in $expectedIds) {
    $matches = @($inputChecks | Where-Object id -eq $id)
    if ($matches.Count -ne 1) { throw "Phase 2 acceptance results must contain $id exactly once; found $($matches.Count)." }
    Assert-ExactProperties $matches[0] @('id', 'result') "Phase 2 acceptance check $id"
    $result = ([string]$matches[0].result).Trim().ToLowerInvariant()
    if ($result -notin $allowedResults) { throw "Check $id has invalid result '$result'. Allowed values: $($allowedResults -join ', ')." }
    $catalogRow = @($catalog | Where-Object id -eq $id)
    [ordered]@{ id = $id; requirement = [string]$catalogRow[0].requirement; result = $result }
}

$artifactPathsByGroup = @{
    'corporate-identity' = @($CorporateIdentityEvidencePath)
    'managed-rdp' = @($ManagedRdpEvidencePath)
    'domain-account' = @($DomainAccountEvidencePath)
    'windows-session' = @($WindowsSessionEvidencePath)
    'redirected-device' = @($RedirectedDeviceEvidencePath)
}
$groupRecords = foreach ($group in $groups) {
    $groupChecks = @($recordedChecks | Where-Object id -in $group.CheckIds)
    $checkResult = if (@($groupChecks | Where-Object result -eq 'fail').Count -gt 0) { 'fail' }
        elseif (@($groupChecks | Where-Object result -eq 'blocked').Count -gt 0) { 'blocked' }
        elseif (@($groupChecks | Where-Object result -ne 'pass').Count -eq 0) { 'pass' }
        else { 'not-run' }
    $evidence = if ($ValidateOnly) { @() } else { @(Get-EvidenceSummaries $artifactPathsByGroup[$group.Id] $group.Id) }
    $result = if ($checkResult -eq 'pass' -and $evidence.Count -eq 0) { 'incomplete-evidence' } else { $checkResult }
    [ordered]@{ id = $group.Id; label = $group.Label; result = $result; checkIds = $group.CheckIds; evidence = $evidence }
}

$allChecksPass = @($recordedChecks | Where-Object result -ne 'pass').Count -eq 0
$allGroupEvidencePresent = @($groupRecords | Where-Object result -ne 'pass').Count -eq 0
$overallResult = if ($allChecksPass -and $allGroupEvidencePresent -and $inputDocument.privacyBoundaryAcknowledged -and $inputDocument.promptOnlyCredentialsConfirmed) { 'pass' } else { 'incomplete' }

if ($ValidateOnly) {
    Write-Host "Phase 2 target-acceptance input schema valid: $($recordedChecks.Count) checks in $($groups.Count) evidence groups; current result $overallResult."
    return
}

if (-not $inputDocument.privacyBoundaryAcknowledged) { throw 'Set privacyBoundaryAcknowledged to true only after confirming raw evidence contains no credentials or identifying target/provider data.' }
if (@($recordedChecks | Where-Object { $_.id -notin $groups[0].CheckIds -and $_.result -eq 'pass' }).Count -gt 0 -and -not $inputDocument.promptOnlyCredentialsConfirmed) {
    throw 'Set promptOnlyCredentialsConfirmed to true only after confirming every RDP target check used a fresh Windows prompt and persisted no password.'
}
if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw "The Phase 2 MSIX package was not found: $PackagePath" }
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$operatingSystem.ProductType) -BuildNumber ([int]$operatingSystem.BuildNumber) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem))) {
    throw 'Phase 2 target evidence requires a Windows 11 x64 client workstation; Windows Server does not satisfy the client acceptance boundary.'
}
if (@($recordedChecks | Where-Object { $_.id -in @('NX-L0054', 'NX-L0055') -and $_.result -eq 'pass' }).Count -gt 0) {
    Add-Type -AssemblyName System.Windows.Forms
    if ([Windows.Forms.Screen]::AllScreens.Count -lt 2) { throw 'All-monitor or selected-monitor checks were marked pass, but this client currently exposes fewer than two monitors.' }
}

$signature = Get-AuthenticodeSignature -LiteralPath $PackagePath
if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
    throw "The Phase 2 MSIX signature is not valid: $($signature.Status)."
}
$package = Get-Item -LiteralPath $PackagePath
$packageHash = Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256
$allEvidenceHashes = @($groupRecords | ForEach-Object evidence | ForEach-Object sha256)
$duplicateEvidence = @($allEvidenceHashes | Group-Object | Where-Object Count -ne 1)
if ($duplicateEvidence.Count -gt 0) { throw 'The same raw evidence artifact cannot satisfy more than one Phase 2 target group.' }
$generatedAt = [DateTimeOffset]::UtcNow
$record = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = $generatedAt.ToString('o')
    scope = 'NextRDP Phase 2 target and external-integration acceptance'
    privacy = 'This record contains controlled results and hashes only. It excludes hostnames, IP addresses, usernames, passwords, provider or tenant identifiers, tokens, machine names, filenames, and free-form notes.'
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
    source = Get-SourceFingerprint $projectRoot
    acknowledgements = [ordered]@{
        privacyBoundary = [bool]$inputDocument.privacyBoundaryAcknowledged
        promptOnlyCredentials = [bool]$inputDocument.promptOnlyCredentialsConfirmed
    }
    groups = $groupRecords
    checks = $recordedChecks
    overallResult = $overallResult
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$outputPath = Join-Path $OutputDirectory "NextRDP-Phase2-Target-Acceptance-$($generatedAt.ToString('yyyyMMdd-HHmmss'))Z.json"
[IO.File]::WriteAllText($outputPath, ($record | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
Write-Host "Phase 2 target-acceptance evidence written: $outputPath"
if ($overallResult -ne 'pass' -and -not $AllowIncomplete) {
    $unresolvedIds = @($recordedChecks | Where-Object result -ne 'pass' | ForEach-Object id)
    $missingGroups = @($groupRecords | Where-Object result -ne 'pass' | ForEach-Object id)
    throw "Phase 2 target acceptance is incomplete. Unresolved checks: $($unresolvedIds -join ', '). Evidence groups not complete: $($missingGroups -join ', ')."
}
if ($overallResult -eq 'pass') { Write-Host 'Phase 2 target and external-integration acceptance passed.' }
else { Write-Warning 'Partial target evidence was recorded, but Phase 2 target acceptance is not complete.' }
