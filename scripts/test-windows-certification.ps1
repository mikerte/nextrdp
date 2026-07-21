[CmdletBinding()]
param(
    [string]$Version = '',
    [switch]$PreflightOnly
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
$release = Get-NextRdpReleaseMetadata -ProjectRoot $root
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $release.MsixVersion }
. (Join-Path $PSScriptRoot 'phase1-platform.ps1')
$packagePath = Join-Path $root "artifacts\msix\NextRDP-$Version-x64.msix"
$certificatePath = Join-Path $root 'artifacts\msix\NextRDP-TestSigning.cer'
$packageName = $release.PackageIdentity
$kitPath = 'C:\Program Files (x86)\Windows Kits\10\App Certification Kit\appcert.exe'
$reportDirectory = Join-Path $root 'artifacts\certification'
$reportPath = Join-Path $reportDirectory 'NextRDP-WACK.xml'
$certificationEvidencePath = Join-Path $reportDirectory 'NextRDP-WACK-evidence.json'
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$operatingSystem = Get-CimInstance -ClassName Win32_OperatingSystem

if (-not (Test-Path -LiteralPath $packagePath)) { throw "Test package not found: $packagePath" }
if (-not (Test-Path -LiteralPath $certificatePath)) { throw "Test-signing certificate not found: $certificatePath" }
if (-not (Test-Path -LiteralPath $kitPath)) { throw 'Windows App Certification Kit was not found. Install the Windows SDK App Certification Kit component.' }
if (-not (Test-NextRdpPhaseOneClientPlatform -ProductType ([int]$operatingSystem.ProductType) -BuildNumber ([int]$operatingSystem.BuildNumber) -Is64BitOperatingSystem ([Environment]::Is64BitOperatingSystem))) {
    throw 'Windows App Certification Kit acceptance requires a Windows 11 x64 client workstation (build 22000 or newer); Windows Server does not satisfy the Phase 1 release target.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $packagePath
if (-not (Test-NextRdpEvaluationSignature $signature)) { throw "The MSIX signature is not valid: $($signature.StatusMessage)" }
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) { throw 'The exported test certificate does not match the MSIX signer.' }
$packageHash = Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
$existingPackage = @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue)
$unpackagedDataPath = Join-Path $env:LOCALAPPDATA 'NextRDP'
$packagedDataPaths = @(Get-ChildItem -LiteralPath (Join-Path $env:LOCALAPPDATA 'Packages') -Directory -Filter "$packageName`_*" -ErrorAction SilentlyContinue)
$hasExistingUserData = (Test-Path -LiteralPath $unpackagedDataPath) -or $packagedDataPaths.Count -gt 0

if ($PreflightOnly)
{
    Write-Host "Windows App Certification Kit preflight passed for Windows $($operatingSystem.Version) x64. Package signature: $($signature.Status); signer certificate matches export. Elevated execution available: $isAdministrator; clean package state: $($existingPackage.Count -eq 0); clean user data state: $(-not $hasExistingUserData)."
    if ($existingPackage.Count -gt 0 -or $hasExistingUserData) { Write-Host 'This account is not an isolated certification user. Use a clean Windows 11 x64 test user.' }
    if (-not $isAdministrator) { Write-Host 'Run this script from an elevated PowerShell session in the active clean Windows 11 x64 user session to produce the certification report with temporary Trusted People trust.' }
    return
}

if (-not $isAdministrator) { throw 'Windows App Certification Kit package testing requires an elevated PowerShell session in an active user session.' }
if ($existingPackage.Count -gt 0) { throw "A $packageName package is already installed. Use a clean test user before certification." }
if ($hasExistingUserData) { throw 'Existing NextRDP user data was found. Use a clean Windows 11 x64 test user; certification will not modify or remove an existing inventory.' }
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
if (Test-Path -LiteralPath $reportPath) { Remove-Item -LiteralPath $reportPath -Force }
if (Test-Path -LiteralPath $certificationEvidencePath) { Remove-Item -LiteralPath $certificationEvidencePath -Force }

$trustedPeopleStore = 'Cert:\LocalMachine\TrustedPeople'
$certificateAddedByTest = $false
try
{
    if (-not (Test-Path -LiteralPath (Join-Path $trustedPeopleStore $certificate.Thumbprint)))
    {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation $trustedPeopleStore | Out-Null
        $certificateAddedByTest = $true
    }

    & $kitPath reset
    if ($LASTEXITCODE -ne 0) { throw "Windows App Certification Kit reset failed with exit code $LASTEXITCODE." }
    & $kitPath test -appxpackagepath $packagePath -reportoutputpath $reportPath
    if ($LASTEXITCODE -ne 0) { throw "Windows App Certification Kit test failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $reportPath)) { throw 'Windows App Certification Kit completed without creating its report.' }
}
finally
{
    foreach ($package in @(Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue))
    {
        Remove-AppxPackage -Package $package.PackageFullName
    }
    if ($certificateAddedByTest)
    {
        $trustedCertificate = Join-Path $trustedPeopleStore $certificate.Thumbprint
        if (Test-Path -LiteralPath $trustedCertificate) { Remove-Item -LiteralPath $trustedCertificate -Force }
    }
}

if (Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue) { throw 'The certification run left the NextRDP test package installed.' }
if ($certificateAddedByTest -and (Test-Path -LiteralPath (Join-Path $trustedPeopleStore $certificate.Thumbprint))) { throw 'The certification run left its temporary development-certificate trust installed.' }

$report = Get-Content -LiteralPath $reportPath -Raw
if ($report -match '(?i)(OVERALL_RESULT|OVERALLRESULT)[^>\r\n]*(FAIL|FAILED)|<(OVERALL_RESULT|OVERALLRESULT)>\s*(FAIL|FAILED)\s*</')
{
    throw "Windows App Certification Kit reported a failure. Review $reportPath."
}
if ($report -notmatch '(?i)(OVERALL_RESULT|OVERALLRESULT)[^>\r\n]*PASS|<(OVERALL_RESULT|OVERALLRESULT)>\s*PASS\s*</')
{
    throw "The certification report was created, but its overall result could not be parsed. Review $reportPath and do not accept certification until the parser recognizes the result."
}
else
{
    $reportHash = Get-FileHash -LiteralPath $reportPath -Algorithm SHA256
    $certificationEvidence = [ordered]@{
        schemaVersion = 2
        completedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        scope = 'NextRDP Windows App Certification Kit acceptance'
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
        report = [ordered]@{
            fileName = [IO.Path]::GetFileName($reportPath)
            sha256 = $reportHash.Hash
            overallResult = 'pass'
        }
        checks = [ordered]@{
            packageCleanup = 'pass'
            temporaryTrustCleanup = 'pass'
        }
    }
    $certificationEvidenceJson = $certificationEvidence | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText($certificationEvidencePath, $certificationEvidenceJson, [Text.UTF8Encoding]::new($false))
    Write-Host "Windows App Certification Kit passed with package and temporary-trust cleanup: $reportPath"
}
