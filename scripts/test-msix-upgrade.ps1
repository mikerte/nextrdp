[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PreviousPackagePath,
    [string]$CurrentVersion = '',
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
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

function Start-And-ClosePackage([string]$ApplicationUserModelId) {
    $startedAt = Get-Date
    Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$ApplicationUserModelId"
    $process = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        Start-Sleep -Milliseconds 500
        $process = Get-Process -Name 'NextRdp.Desktop' -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -ge $startedAt } | Sort-Object StartTime -Descending | Select-Object -First 1
        if ($null -ne $process -and $process.MainWindowHandle -ne 0) { break }
    }
    if ($null -eq $process -or $process.MainWindowHandle -eq 0 -or -not $process.Responding) {
        throw 'The installed NextRDP package did not expose a responsive desktop window.'
    }
    Start-Sleep -Seconds 3
    if (-not $process.CloseMainWindow()) { throw 'NextRDP did not accept a normal main-window close request.' }
    Wait-Process -Id $process.Id -Timeout 15 -ErrorAction Stop
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
$isClean = $existingPackage.Count -eq 0 -and -not (Test-Path -LiteralPath $unpackagedDataPath) -and $packageDataPaths.Count -eq 0
if ($PreflightOnly) {
    Write-Host "MSIX upgrade preflight passed: $previousVersion to $newVersion; identity, publisher, architecture, and signer are stable. Elevated execution available: $isAdministrator; clean user state: $isClean."
    return
}
if (-not $isAdministrator) { throw 'MSIX upgrade acceptance requires an elevated PowerShell session for temporary certificate trust.' }
if (-not $isClean) { throw 'Use a clean Windows 11 x64 test user; the upgrade test will not modify an existing installation or inventory.' }

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($certificate.Thumbprint -ne $current.Signature.SignerCertificate.Thumbprint) { throw 'The exported test certificate does not match the package signer.' }
$trustedPeopleStore = 'Cert:\LocalMachine\TrustedPeople'
$certificateAdded = $false
$packageDataPath = $null
$markerPath = $null
$databasePath = $null
$evidenceDirectory = Join-Path $root 'artifacts\acceptance'
$evidencePath = Join-Path $evidenceDirectory 'msix-upgrade-smoke.json'
New-Item -ItemType Directory -Path $evidenceDirectory -Force | Out-Null
if (Test-Path -LiteralPath $evidencePath) { Remove-Item -LiteralPath $evidencePath -Force }

try {
    if (-not (Test-Path -LiteralPath (Join-Path $trustedPeopleStore $certificate.Thumbprint))) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation $trustedPeopleStore | Out-Null
        $certificateAdded = $true
    }
    Add-AppxPackage -Path $previousPackage
    $installed = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if ([Version]$installed.Version -ne $previousVersion) { throw 'The previous package version was not installed.' }
    $packageDataPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Packages') $installed.PackageFamilyName
    Start-And-ClosePackage "$($installed.PackageFamilyName)!App"
    $databasePath = Get-ChildItem -LiteralPath $packageDataPath -Recurse -File -Filter 'nextrdp.db' -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($databasePath)) { throw 'The previous version did not create its local inventory database.' }
    $markerPath = Join-Path (Split-Path -Parent $databasePath) 'release-upgrade-marker.json'
    [IO.File]::WriteAllText($markerPath, '{"purpose":"package-data-upgrade-validation"}', [Text.UTF8Encoding]::new($false))

    Add-AppxPackage -Path $currentPackage -ForceUpdateFromAnyVersion
    $upgraded = Get-AppxPackage -Name $packageName -ErrorAction Stop
    if ([Version]$upgraded.Version -ne $newVersion) { throw 'The current package version was not installed as an upgrade.' }
    if ($upgraded.PackageFamilyName -ne $installed.PackageFamilyName) { throw 'The package family changed during upgrade.' }
    if (-not (Test-Path -LiteralPath $databasePath) -or -not (Test-Path -LiteralPath $markerPath)) {
        throw 'Package-local inventory data did not survive the MSIX upgrade.'
    }
    Start-And-ClosePackage "$($upgraded.PackageFamilyName)!App"
    if (-not (Test-Path -LiteralPath $databasePath) -or -not (Test-Path -LiteralPath $markerPath)) {
        throw 'Package-local inventory data was not available after launching the upgraded application.'
    }
}
finally {
    foreach ($package in @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)) { Remove-AppxPackage -Package $package.PackageFullName }
    if ($certificateAdded) {
        $trustedCertificate = Join-Path $trustedPeopleStore $certificate.Thumbprint
        if (Test-Path -LiteralPath $trustedCertificate) { Remove-Item -LiteralPath $trustedCertificate -Force }
    }
}

if (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue) { throw 'NextRDP remained installed after the upgrade test cleanup.' }
for ($attempt = 0; $null -ne $packageDataPath -and (Test-Path -LiteralPath $packageDataPath) -and $attempt -lt 10; $attempt++) { Start-Sleep -Milliseconds 500 }
if ($null -ne $packageDataPath -and (Test-Path -LiteralPath $packageDataPath)) { throw 'Package data remained after upgrade-test uninstall.' }

$evidence = [ordered]@{
    schemaVersion = 1
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'NextRDP MSIX identity, data-persistence, launch, and uninstall upgrade smoke'
    platform = [ordered]@{ product = [string]$operatingSystem.Caption; version = [string]$operatingSystem.Version; build = [int]$operatingSystem.BuildNumber; architecture = 'x64' }
    package = [ordered]@{ identity = $packageName; publisher = $release.PackagePublisher; previousVersion = $previousVersion.ToString(4); currentVersion = $newVersion.ToString(4); previousSha256 = $previous.Hash; currentSha256 = $current.Hash }
    checks = [ordered]@{ identityStable = 'pass'; signerStable = 'pass'; previousLaunch = 'pass'; upgrade = 'pass'; inventoryDataPreserved = 'pass'; upgradedLaunch = 'pass'; uninstall = 'pass'; packageDataCleanup = 'pass'; temporaryTrustCleanup = 'pass' }
}
[IO.File]::WriteAllText($evidencePath, ($evidence | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Write-Host "MSIX upgrade acceptance passed: $previousVersion to $newVersion with package-local inventory data preserved and cleanup complete."
