[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackagePath,
    [string]$CurrentVersion = '',
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
. (Join-Path $PSScriptRoot 'package-test-isolation.ps1')
. (Join-Path $PSScriptRoot 'phase1-platform.ps1')
$release = Get-NextRdpReleaseMetadata -ProjectRoot $root
if ([string]::IsNullOrWhiteSpace($CurrentVersion)) { $CurrentVersion = $release.MsixVersion }
$previousPackage = [IO.Path]::GetFullPath($PreviousPackagePath)
$currentPackage = Join-Path $root "artifacts\msix\NextRDP-$CurrentVersion-x64.msix"
$certificatePath = Join-Path $root 'artifacts\msix\NextRDP-TestSigning.cer'
$packageName = $release.PackageIdentity

foreach ($requiredPath in @($previousPackage, $currentPackage, $certificatePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Upgrade input is missing: $requiredPath" }
}

function Get-PackageDetails([string]$Path) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $manifestEntry = $archive.GetEntry('AppxManifest.xml')
        if ($null -eq $manifestEntry) { throw "Package '$Path' does not contain AppxManifest.xml." }
        $reader = [IO.StreamReader]::new($manifestEntry.Open())
        try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    }
    finally { $archive.Dispose() }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($null -eq $signature.SignerCertificate) { throw "Package '$Path' does not have an Authenticode signer." }
    [pscustomobject]@{ Identity = $manifest.Package.Identity; Signature = $signature; Hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}

function Assert-PackageSignature([object]$Details, [string]$Label) {
    $expectedUntrustedRoot = $Details.Signature.Status -eq 'UnknownError' -and $Details.Signature.StatusMessage -match '(?i)root certificate.*not trusted'
    if ($Details.Signature.Status -ne 'Valid' -and -not $expectedUntrustedRoot) {
        throw "$Label signature is not acceptable: $($Details.Signature.StatusMessage)"
    }
}

function Start-And-ClosePackage([string]$ExecutablePath, [string]$DataPath) {
    $environmentVariable = 'NEXTRDP_TEST_DATA_DIRECTORY'
    $previousDataPath = [Environment]::GetEnvironmentVariable($environmentVariable, [EnvironmentVariableTarget]::Process)
    $process = $null
    try {
        [Environment]::SetEnvironmentVariable($environmentVariable, $DataPath, [EnvironmentVariableTarget]::Process)
        $process = Start-Process -FilePath $ExecutablePath -PassThru
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
            if ($process.MainWindowHandle -ne 0 -and $process.Responding) { break }
        }
        if ($process.MainWindowHandle -eq 0 -or -not $process.Responding) {
            throw 'The installed NextRDP package did not expose a responsive desktop window within 30 seconds.'
        }
        Start-Sleep -Seconds 3
        if (-not $process.CloseMainWindow()) { throw 'NextRDP did not accept a normal main-window close request.' }
        Wait-Process -Id $process.Id -Timeout 15 -ErrorAction Stop
    }
    finally {
        [Environment]::SetEnvironmentVariable($environmentVariable, $previousDataPath, [EnvironmentVariableTarget]::Process)
        if ($null -ne $process -and -not $process.HasExited) {
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { Stop-Process -Id $process.Id -Force }
        }
    }
}

$previous = Get-PackageDetails $previousPackage
$current = Get-PackageDetails $currentPackage
Assert-PackageSignature $previous 'Previous package'
Assert-PackageSignature $current 'Current package'
foreach ($details in @($previous, $current)) {
    if ($details.Identity.Name -ne $packageName -or $details.Identity.Publisher -ne $release.PackagePublisher -or $details.Identity.ProcessorArchitecture -ne 'x64') {
        throw 'Upgrade packages must use the same expected identity, publisher, and x64 architecture.'
    }
}
$previousVersion = [Version]$previous.Identity.Version
$newVersion = [Version]$current.Identity.Version
if ($newVersion.ToString(4) -ne $CurrentVersion -or $previousVersion -ge $newVersion) {
    throw "Upgrade versions must increase; received $previousVersion to $newVersion."
}
if ($previous.Signature.SignerCertificate.Thumbprint -ne $current.Signature.SignerCertificate.Thumbprint) {
    throw 'Upgrade packages must be signed by the same certificate.'
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$operatingSystem.ProductType) -BuildNumber ([int]$operatingSystem.BuildNumber) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem))) {
    throw 'MSIX upgrade acceptance requires a Windows 11 x64 client workstation.'
}
$existingPackage = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
$unpackagedDataPath = Join-Path $env:LOCALAPPDATA 'NextRDP'
$packageDataPaths = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter "$packageName`_*" -ErrorAction SilentlyContinue)
$hasUnpackagedData = Test-Path -LiteralPath $unpackagedDataPath -PathType Container
$unpackagedDataFingerprintBefore = Get-NextRdpDirectoryFingerprint -Path $unpackagedDataPath
$isPackageNamespaceClean = $existingPackage.Count -eq 0 -and $packageDataPaths.Count -eq 0
if ($PreflightOnly) {
    Write-Host "MSIX upgrade preflight passed: $previousVersion to $newVersion; identity, publisher, architecture, and signer are stable. Elevated execution available: $isAdministrator; clean package namespace: $isPackageNamespaceClean; unpackaged data present and fingerprinted: $hasUnpackagedData."
    return
}
if (-not $isAdministrator) { throw 'MSIX upgrade acceptance requires an elevated PowerShell session for temporary machine-level Trusted People trust.' }
if (-not $isPackageNamespaceClean) { throw 'Use a clean NextRDP.Client package namespace; the upgrade test will not modify an existing package installation or data.' }
if (Get-Process -Name 'NextRdp.Desktop' -ErrorAction SilentlyContinue) { throw 'Close all running NextRDP processes before package acceptance.' }

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.Thumbprint -ne $current.Signature.SignerCertificate.Thumbprint) { throw 'The exported test certificate does not match the package signer.' }
$trustedPeopleStore = 'Cert:\LocalMachine\TrustedPeople'
$certificateAdded = $false
$packageDataPath = $null
$markerPath = $null
$databasePath = $null
$evidenceDirectory = Join-Path $root 'artifacts\acceptance'
$evidencePath = Join-Path $evidenceDirectory 'msix-upgrade-smoke.json'
$isolatedDataPath = Join-Path $evidenceDirectory "msix-upgrade-data-$([Guid]::NewGuid().ToString('N'))"
$resolvedEvidenceRoot = [IO.Path]::GetFullPath($evidenceDirectory).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$resolvedIsolatedDataPath = [IO.Path]::GetFullPath($isolatedDataPath)
if (-not $resolvedIsolatedDataPath.StartsWith($resolvedEvidenceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'The isolated MSIX upgrade data path must remain under the acceptance artifact directory.' }
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $resolvedIsolatedDataPath -Force | Out-Null
if (Test-Path -LiteralPath $evidencePath) { Remove-Item -LiteralPath $evidencePath -Force }

try {
    if (-not (Test-Path -LiteralPath (Join-Path $trustedPeopleStore $certificate.Thumbprint))) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation $trustedPeopleStore | Out-Null
        $certificateAdded = $true
    }
    foreach ($packagePath in @($previousPackage, $currentPackage)) {
        $trustedSignature = Get-AuthenticodeSignature -LiteralPath $packagePath
        if ($trustedSignature.Status -ne 'Valid' -or $trustedSignature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
            throw "The package signature did not become valid after applying temporary machine-level trust: $packagePath"
        }
    }
    Add-AppxPackage -Path $previousPackage
    $installed = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if ([Version]$installed.Version -ne $previousVersion) { throw 'The previous package version was not installed.' }
    $packageDataPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Packages') $installed.PackageFamilyName
    $previousExecutable = Join-Path $installed.InstallLocation 'NextRdp.Desktop.exe'
    if (-not (Test-Path -LiteralPath $previousExecutable -PathType Leaf)) { throw 'The previous installed package does not contain NextRdp.Desktop.exe.' }
    Start-And-ClosePackage $previousExecutable $resolvedIsolatedDataPath
    $databasePath = Join-Path $resolvedIsolatedDataPath 'nextrdp.db'
    if (-not (Test-Path -LiteralPath $databasePath -PathType Leaf)) { throw 'The previous version did not create its isolated local inventory database.' }
    $markerPath = Join-Path (Split-Path -Parent $databasePath) 'release-upgrade-marker.json'
    [IO.File]::WriteAllText($markerPath, '{"purpose":"package-data-upgrade-validation"}', [Text.UTF8Encoding]::new($false))

    Add-AppxPackage -Path $currentPackage -ForceUpdateFromAnyVersion
    $upgraded = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if ([Version]$upgraded.Version -ne $newVersion) { throw 'The current package version was not installed as an upgrade.' }
    if ($upgraded.PackageFamilyName -ne $installed.PackageFamilyName) { throw 'The package family changed during upgrade.' }
    if (-not (Test-Path -LiteralPath $databasePath) -or -not (Test-Path -LiteralPath $markerPath)) {
        throw 'The isolated local inventory data did not survive the MSIX upgrade.'
    }
    $upgradedExecutable = Join-Path $upgraded.InstallLocation 'NextRdp.Desktop.exe'
    if (-not (Test-Path -LiteralPath $upgradedExecutable -PathType Leaf)) { throw 'The upgraded package does not contain NextRdp.Desktop.exe.' }
    Start-And-ClosePackage $upgradedExecutable $resolvedIsolatedDataPath
    if (-not (Test-Path -LiteralPath $databasePath) -or -not (Test-Path -LiteralPath $markerPath)) {
        throw 'The isolated local inventory data was not available after launching the upgraded application.'
    }
}
finally {
    foreach ($package in @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)) { Remove-AppxPackage -Package $package.PackageFullName }
    if ($certificateAdded) {
        $trustedCertificate = Join-Path $trustedPeopleStore $certificate.Thumbprint
        if (Test-Path -LiteralPath $trustedCertificate) { Remove-Item -LiteralPath $trustedCertificate -Force }
    }
    $unpackagedDataUntouched = (Get-NextRdpDirectoryFingerprint -Path $unpackagedDataPath) -eq $unpackagedDataFingerprintBefore
    if (Test-Path -LiteralPath $resolvedIsolatedDataPath) { Remove-Item -LiteralPath $resolvedIsolatedDataPath -Recurse -Force }
    if (-not $unpackagedDataUntouched) {
        throw 'The pre-existing unpackaged NextRDP data changed during isolated package upgrade acceptance.'
    }
}

$packageStillInstalled = $true
$packageDataStillPresent = $true
for ($attempt = 0; $attempt -lt 120; $attempt++) {
    $packageStillInstalled = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue).Count -gt 0
    $packageDataStillPresent = $null -ne $packageDataPath -and (Test-Path -LiteralPath $packageDataPath)
    if (-not $packageStillInstalled -and -not $packageDataStillPresent) { break }
    Start-Sleep -Milliseconds 500
}
if ($packageStillInstalled) { throw 'NextRDP remained installed after the 60-second upgrade-test cleanup window.' }
if ($packageDataStillPresent) { throw 'Package data remained after the 60-second upgrade-test cleanup window.' }
if (Test-Path -LiteralPath $resolvedIsolatedDataPath) { throw 'The disposable upgrade inventory remained after cleanup.' }

$evidence = [ordered]@{
    schemaVersion = 1
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'NextRDP MSIX identity, data-persistence, launch, and uninstall upgrade smoke'
    isolation = [ordered]@{ mode = 'isolated-test-data'; trustScope = 'LocalMachine'; preexistingUnpackagedData = if ($hasUnpackagedData) { 'present-and-fingerprinted' } else { 'absent' } }
    platform = [ordered]@{ product = [string]$operatingSystem.Caption; version = [string]$operatingSystem.Version; build = [int]$operatingSystem.BuildNumber; architecture = 'x64' }
    package = [ordered]@{ identity = $packageName; publisher = $release.PackagePublisher; previousVersion = $previousVersion.ToString(4); currentVersion = $newVersion.ToString(4); previousSha256 = $previous.Hash; currentSha256 = $current.Hash }
    checks = [ordered]@{ identityStable = 'pass'; signerStable = 'pass'; previousLaunch = 'pass'; upgrade = 'pass'; inventoryDataPreserved = 'pass'; upgradedLaunch = 'pass'; uninstall = 'pass'; packageDataCleanup = 'pass'; isolatedTestDataCleanup = 'pass'; temporaryTrustCleanup = 'pass'; unpackagedDataUntouched = if ($hasUnpackagedData) { 'pass' } else { 'not-applicable' } }
}
[IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "MSIX upgrade acceptance passed: $previousVersion to $newVersion with isolated local inventory data preserved and cleanup complete."
