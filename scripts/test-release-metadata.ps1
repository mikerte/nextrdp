[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
$release = Get-NextRdpReleaseMetadata -ProjectRoot $root

$testSigner = [pscustomobject]@{ Thumbprint = 'TEST' }
$validSignature = [pscustomobject]@{ Status = 'Valid'; StatusMessage = 'Valid'; SignerCertificate = $testSigner }
$untrustedEvaluationSignature = [pscustomobject]@{ Status = 'UnknownError'; StatusMessage = 'A certificate chain processed, but terminated in a root certificate which is not trusted by the trust provider.'; SignerCertificate = $testSigner }
$invalidSignature = [pscustomobject]@{ Status = 'HashMismatch'; StatusMessage = 'The file hash is not valid.'; SignerCertificate = $testSigner }
if (-not (Test-NextRdpEvaluationSignature $validSignature) -or -not (Test-NextRdpEvaluationSignature $untrustedEvaluationSignature) -or (Test-NextRdpEvaluationSignature $invalidSignature)) {
    throw 'Evaluation-signature status validation did not preserve the fail-closed trust boundary.'
}

[xml]$directoryProps = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw -Encoding utf8
$releaseImport = @($directoryProps.Project.Import | Where-Object { $_.Project -eq '$(MSBuildThisFileDirectory)eng\Release.props' })
if ($releaseImport.Count -ne 1) { throw 'Directory.Build.props must import eng\Release.props exactly once.' }

$adminPackage = Get-Content -LiteralPath (Join-Path $root 'src\NextRdp.Admin\package.json') -Raw -Encoding utf8 | ConvertFrom-Json
$adminLockText = Get-Content -LiteralPath (Join-Path $root 'src\NextRdp.Admin\package-lock.json') -Raw -Encoding utf8
if ($adminPackage.version -ne $release.ProductVersion) { throw "Administration package version '$($adminPackage.version)' does not match '$($release.ProductVersion)'." }
$topLevelLockVersion = [regex]::Match($adminLockText, '^\s*\{\s*"name"\s*:\s*"[^"]+"\s*,\s*"version"\s*:\s*"([^"]+)"', [Text.RegularExpressions.RegexOptions]::Singleline)
$rootPackageVersion = [regex]::Match($adminLockText, '"packages"\s*:\s*\{\s*""\s*:\s*\{[\s\S]*?"version"\s*:\s*"([^"]+)"')
if (-not $topLevelLockVersion.Success -or -not $rootPackageVersion.Success -or $topLevelLockVersion.Groups[1].Value -ne $release.ProductVersion -or $rootPackageVersion.Groups[1].Value -ne $release.ProductVersion) {
    throw 'Administration package-lock versions do not match the release product version.'
}

$moduleManifest = Import-PowerShellDataFile -LiteralPath (Join-Path $root 'src\NextRdp.PowerShell\NextRdp.psd1')
if ([string]$moduleManifest.ModuleVersion -ne $release.ProductVersion) {
    throw "PowerShell module version '$($moduleManifest.ModuleVersion)' does not match '$($release.ProductVersion)'."
}

$releaseNotesPath = Join-Path $root "docs\releases\$($release.ReleaseTag).md"
if (-not (Test-Path -LiteralPath $releaseNotesPath -PathType Leaf)) { throw "Release notes are missing: $releaseNotesPath" }
$releaseNotes = Get-Content -LiteralPath $releaseNotesPath -Raw -Encoding utf8
if ($releaseNotes -notmatch [regex]::Escape($release.ReleaseVersion) -or $releaseNotes -notmatch '(?i)source-only') {
    throw 'Release notes must identify the release version and its source-only distribution boundary.'
}

$stalePackageReferences = @(Get-ChildItem -LiteralPath @(
    (Join-Path $root 'scripts'),
    (Join-Path $root 'docs'),
    (Join-Path $root '.github')
) -Recurse -File | Where-Object { $_.Extension -in @('.ps1', '.md', '.yml', '.yaml') } |
    Select-String -Pattern 'NextRDP-0\.1\.0\.0-x64\.msix' |
    Where-Object { $_.Path -notlike '*docs\releases\*' })
if ($stalePackageReferences.Count -gt 0) {
    throw "A stale hard-coded package path remains at $($stalePackageReferences[0].Path):$($stalePackageReferences[0].LineNumber)."
}

Write-Host "Release metadata passed: $($release.ReleaseTag), product $($release.ProductVersion), MSIX $($release.MsixVersion), identity $($release.PackageIdentity)."
