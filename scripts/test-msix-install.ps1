[CmdletBinding()]
param(
    [string]$Version = '0.1.0.0',
    [switch]$PreflightOnly,
    [switch]$ManualAcceptance,
    [ValidateRange(5, 180)][int]$ManualTimeoutMinutes = 60
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'phase1-platform.ps1')
$packagePath = Join-Path $root "artifacts\msix\NextRDP-$Version-x64.msix"
$certificatePath = Join-Path $root 'artifacts\msix\NextRDP-TestSigning.cer'
$packageName = 'NextRDP.Client'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem

if (-not (Test-Path -LiteralPath $packagePath)) { throw "Test package not found: $packagePath" }
if (-not (Test-Path -LiteralPath $certificatePath)) { throw "Test-signing certificate not found: $certificatePath" }
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$operatingSystem.ProductType) -BuildNumber ([int]$operatingSystem.BuildNumber) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem))) {
    throw 'MSIX acceptance requires a Windows 11 x64 client workstation (build 22000 or newer); Windows Server does not satisfy the Phase 1 release target.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if ($signature.Status -ne 'Valid') { throw "The MSIX signature is not valid: $($signature.StatusMessage)" }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The exported test certificate does not match the MSIX signer.' }
$packageHash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
try
{
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The package does not contain AppxManifest.xml.' }
    $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$manifest = $manifestReader.ReadToEnd() } finally { $manifestReader.Dispose() }
}
finally
{
    $archive.Dispose()
}

$identity = $manifest.Package.Identity
if ($identity.Name -ne $packageName) { throw "Unexpected package identity '$($identity.Name)'; expected '$packageName'." }
if ($identity.ProcessorArchitecture -ne 'x64') { throw "Unexpected package architecture '$($identity.ProcessorArchitecture)'; expected 'x64'." }
if ($identity.Version -ne $Version) { throw "Unexpected package version '$($identity.Version)'; expected '$Version'." }
$existingPackage = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
if ($existingPackage.Count -gt 0) { throw "A $packageName package is already installed. Use a clean test user or remove it deliberately before running this isolated acceptance test." }
$unpackagedDataPath = Join-Path $env:LOCALAPPDATA 'NextRDP'
$packagedDataPaths = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter "$packageName`_*" -ErrorAction SilentlyContinue)
$hasExistingUserData = (Test-Path -LiteralPath $unpackagedDataPath) -or $packagedDataPaths.Count -gt 0

if ($PreflightOnly)
{
    Write-Host "MSIX package preflight passed for Windows $($operatingSystem.Version) x64, package $($identity.Name) $($identity.Version) $($identity.ProcessorArchitecture), signature $($signature.Status). Elevated execution available: $isAdministrator; clean user data state: $(-not $hasExistingUserData)."
    if ($hasExistingUserData) { Write-Host 'This account contains existing NextRDP data. The mutating acceptance run will refuse to proceed; use a clean Windows 11 x64 test user.' }
    if (-not $isAdministrator) { Write-Host 'Run this script from an elevated PowerShell session on the clean Windows 11 x64 test machine to perform install, launch, uninstall, and trust cleanup.' }
    return
}

if (-not $isAdministrator) { throw 'MSIX install acceptance requires an elevated PowerShell session to trust and later remove the development certificate at machine scope.' }
if ($hasExistingUserData) { throw 'Existing NextRDP user data was found. Use a clean Windows 11 x64 test user; this acceptance script will not modify or remove existing inventory.' }

$stores = @('Cert:\LocalMachine\TrustedPeople')
$storesAddedByTest = [Collections.Generic.List[string]]::new()
$installedPackage = $null
$launchedProcess = $null
$testPackagedDataPath = $null
$acceptanceDirectory = Join-Path $root 'artifacts\acceptance'
$installEvidencePath = Join-Path $acceptanceDirectory 'msix-install-smoke.json'
$manualWindowStartedAt = $null
$manualWindowEndedAt = $null
$manualWindowSeconds = $null

New-Item -ItemType Directory -Path $acceptanceDirectory -Force | Out-Null
if (Test-Path -LiteralPath $installEvidencePath) { Remove-Item -LiteralPath $installEvidencePath -Force }

try
{
    foreach ($store in $stores)
    {
        if (-not (Test-Path -LiteralPath (Join-Path $store $certificate.Thumbprint)))
        {
            Import-Certificate -FilePath $certificatePath -CertStoreLocation $store | Out-Null
            $storesAddedByTest.Add($store)
        }
    }

    Add-AppxPackage -Path $packagePath -ForceUpdateFromAnyVersion
    $installedPackage = Get-AppxPackage -Name $packageName -ErrorAction Stop
    $testPackagedDataPath = Join-Path (Join-Path $env:LOCALAPPDATA 'Packages') $installedPackage.PackageFamilyName
    $installedExecutable = Join-Path $installedPackage.InstallLocation 'NextRdp.Desktop.exe'
    if (-not (Test-Path -LiteralPath $installedExecutable)) { throw 'The installed package does not contain NextRdp.Desktop.exe.' }

    $launchStartedAt = Get-Date
    $applicationUserModelId = "$($installedPackage.PackageFamilyName)!App"
    Start-Process -FilePath 'explorer.exe' -ArgumentList "shell:AppsFolder\$applicationUserModelId"
    for ($attempt = 0; $attempt -lt 15; $attempt++)
    {
        Start-Sleep -Milliseconds 500
        $launchedProcess = Get-Process -Name 'NextRdp.Desktop' -ErrorAction SilentlyContinue |
            Where-Object { $_.StartTime -ge $launchStartedAt } |
            Sort-Object StartTime -Descending |
            Select-Object -First 1
        if ($null -ne $launchedProcess -and $launchedProcess.MainWindowHandle -ne 0) { break }
    }
    if ($null -eq $launchedProcess -or $launchedProcess.MainWindowHandle -eq 0) { throw 'The installed NextRDP package did not expose a desktop window.' }
    if (-not $launchedProcess.Responding) { throw 'The installed NextRDP process is not responding.' }
    if ($ManualAcceptance)
    {
        $manualWindowStartedAt = [DateTimeOffset]::UtcNow
        Write-Host "NextRDP is installed and open for the Phase 1 walkthrough. Complete the fixed acceptance checklist without recording credentials, then close NextRDP normally within $ManualTimeoutMinutes minute(s)."
        Wait-Process -Id $launchedProcess.Id -Timeout ($ManualTimeoutMinutes * 60) -ErrorAction Stop
        $manualWindowEndedAt = [DateTimeOffset]::UtcNow
        $manualWindowSeconds = [int][Math]::Round(($manualWindowEndedAt - $manualWindowStartedAt).TotalSeconds)
        $launchedProcess.Refresh()
        if ($launchedProcess.ExitCode -ne 0) { throw "NextRDP exited with code $($launchedProcess.ExitCode); do not accept the manual walkthrough as a normal close." }
    }
    else
    {
        Start-Sleep -Seconds 5
        $launchedProcess.Refresh()
        if (-not $launchedProcess.Responding) { throw 'The installed NextRDP process stopped responding during the smoke interval.' }
        if (-not $launchedProcess.CloseMainWindow()) { throw 'NextRDP did not accept a normal main-window close request.' }
        Wait-Process -Id $launchedProcess.Id -Timeout 10 -ErrorAction Stop
    }
}
finally
{
    $remainingProcess = Get-Process -Name 'NextRdp.Desktop' -ErrorAction SilentlyContinue |
        Where-Object { $null -eq $launchedProcess -or $_.Id -eq $launchedProcess.Id } |
        Select-Object -First 1
    if ($null -ne $remainingProcess) { $null = $remainingProcess.CloseMainWindow() }

    $installedPackages = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
    foreach ($package in $installedPackages) { Remove-AppxPackage -Package $package.PackageFullName }
    foreach ($store in $storesAddedByTest)
    {
        $trustedCertificate = Join-Path $store $certificate.Thumbprint
        if (Test-Path -LiteralPath $trustedCertificate) { Remove-Item -LiteralPath $trustedCertificate -Force }
    }
}

if (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue) { throw 'NextRDP remained installed after the uninstall acceptance step.' }
for ($attempt = 0; $null -ne $testPackagedDataPath -and (Test-Path -LiteralPath $testPackagedDataPath) -and $attempt -lt 10; $attempt++)
{
    Start-Sleep -Milliseconds 500
}
if ($null -ne $testPackagedDataPath -and (Test-Path -LiteralPath $testPackagedDataPath)) { throw 'NextRDP packaged user data remained after uninstall; do not accept the clean-install result.' }
$installEvidence = [ordered]@{
    schemaVersion = 2
    completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    scope = 'NextRDP clean MSIX install and removal acceptance'
    mode = if ($ManualAcceptance) { 'manual-acceptance' } else { 'automated-smoke' }
    platform = [ordered]@{
        product = [string]$operatingSystem.Caption
        version = [string]$operatingSystem.Version
        build = [int]$operatingSystem.BuildNumber
        productType = [int]$operatingSystem.ProductType
        architecture = 'x64'
    }
    package = [ordered]@{
        fileName = [IO.Path]::GetFileName($packagePath)
        version = $Version
        sha256 = $packageHash.Hash
        signatureStatus = $signature.Status.ToString()
        signerThumbprint = $signature.SignerCertificate.Thumbprint
    }
    checks = [ordered]@{
        install = 'pass'
        launchResponsive = 'pass'
        normalClose = 'pass'
        manualAcceptanceWindow = if ($ManualAcceptance) { 'pass' } else { 'not-run' }
        uninstall = 'pass'
        packagedUserDataCleanup = 'pass'
        temporaryTrustCleanup = 'pass'
    }
    manualAcceptance = if ($ManualAcceptance) {
        [ordered]@{
            startedAtUtc = $manualWindowStartedAt.ToString('O')
            endedAtUtc = $manualWindowEndedAt.ToString('O')
            durationSeconds = $manualWindowSeconds
        }
    } else { $null }
}
$installEvidenceJson = $installEvidence | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($installEvidencePath, $installEvidenceJson, [Text.UTF8Encoding]::new($false))
if ($ManualAcceptance) {
    Write-Host 'MSIX manual-acceptance window, normal close, uninstall, packaged-data cleanup, and temporary-trust cleanup passed.'
}
else {
    Write-Host 'MSIX install, responsive launch, normal close, uninstall, and temporary trust cleanup passed.'
}
