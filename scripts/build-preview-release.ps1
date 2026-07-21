[CmdletBinding()]
param(
    [switch]$SkipValidation,
    [switch]$AllowDirty
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'release-metadata.ps1')
$release = Get-NextRdpReleaseMetadata -ProjectRoot $root

$gitStatus = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect the Git working tree.' }
if (-not $AllowDirty -and $gitStatus.Count -gt 0) {
    throw 'The preview release build requires a clean working tree. Commit the intended source or use -AllowDirty only for local development validation.'
}

& (Join-Path $PSScriptRoot 'test-release-metadata.ps1')
if ($SkipValidation) {
    & (Join-Path $PSScriptRoot 'publish-windows.ps1') -Configuration Release
    & (Join-Path $PSScriptRoot 'package-msix.ps1')
}
else {
    & (Join-Path $PSScriptRoot 'check-phase2.ps1') -Configuration Release -Package
}

$publishDirectory = Join-Path $root 'artifacts\publish\win-x64'
$executablePath = Join-Path $publishDirectory 'NextRdp.Desktop.exe'
$packagePath = Join-Path $root "artifacts\msix\NextRDP-$($release.MsixVersion)-x64.msix"
$certificatePath = Join-Path $root 'artifacts\msix\NextRDP-TestSigning.cer'
foreach ($requiredPath in @($executablePath, $packagePath, $certificatePath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw "Release artifact is missing: $requiredPath" }
}

$executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
if ($executableVersion.FileVersion -ne $release.MsixVersion) {
    throw "Desktop file version '$($executableVersion.FileVersion)' does not match '$($release.MsixVersion)'."
}
if (-not $executableVersion.ProductVersion.StartsWith($release.ReleaseVersion, [StringComparison]::Ordinal)) {
    throw "Desktop product version '$($executableVersion.ProductVersion)' does not start with '$($release.ReleaseVersion)'."
}

$releaseDirectory = Join-Path $root "artifacts\release\$($release.ReleaseTag)"
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedReleaseDirectory = [IO.Path]::GetFullPath($releaseDirectory)
if (-not $resolvedReleaseDirectory.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must remain inside the project directory.'
}
if (Test-Path -LiteralPath $resolvedReleaseDirectory) { Remove-Item -LiteralPath $resolvedReleaseDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $resolvedReleaseDirectory -Force | Out-Null

Add-Type -AssemblyName System.IO.Compression
$archivePath = Join-Path $resolvedReleaseDirectory "NextRDP-$($release.ReleaseVersion)-win-x64.zip"
$archiveStream = [IO.File]::Open($archivePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $archive = [IO.Compression.ZipArchive]::new($archiveStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Sort-Object FullName) {
            $relativePath = $file.FullName.Substring($publishDirectory.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = $archive.CreateEntry($relativePath, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $inputStream = $file.OpenRead()
            $entryStream = $entry.Open()
            try { $inputStream.CopyTo($entryStream) } finally { $entryStream.Dispose(); $inputStream.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $archiveStream.Dispose() }

$localMsixPath = Join-Path $resolvedReleaseDirectory ([IO.Path]::GetFileName($packagePath))
$localCertificatePath = Join-Path $resolvedReleaseDirectory ([IO.Path]::GetFileName($certificatePath))
Copy-Item -LiteralPath $packagePath -Destination $localMsixPath -Force
Copy-Item -LiteralPath $certificatePath -Destination $localCertificatePath -Force
$notesSource = Join-Path $root "docs\releases\$($release.ReleaseTag).md"
Copy-Item -LiteralPath $notesSource -Destination (Join-Path $resolvedReleaseDirectory 'RELEASE-NOTES.md') -Force

$artifactFiles = @($archivePath, $localMsixPath, $localCertificatePath)
$artifactRecords = @($artifactFiles | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    $hash = Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256
    [ordered]@{ fileName = $item.Name; sizeBytes = $item.Length; sha256 = $hash.Hash }
})
$hashLines = @($artifactRecords | ForEach-Object { "$($_.sha256) *$($_.fileName)" })
$hashLines | Set-Content -LiteralPath (Join-Path $resolvedReleaseDirectory 'SHA256SUMS.txt') -Encoding ascii

$commitSha = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commitSha)) { throw 'Unable to resolve the release source commit.' }
$manifest = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    releaseVersion = $release.ReleaseVersion
    releaseTag = $release.ReleaseTag
    channel = $release.ReleaseChannel
    sourceCommit = $commitSha
    sourceTree = if ($gitStatus.Count -eq 0) { 'clean' } else { 'dirty-development-build' }
    package = [ordered]@{
        identity = $release.PackageIdentity
        publisher = $release.PackagePublisher
        version = $release.MsixVersion
        architecture = 'x64'
        signing = 'local-test-certificate'
    }
    publicDistribution = [ordered]@{
        sourceOnly = $true
        binaryAssetsPublished = $false
        reason = 'Production certificate custody and release approval remain Phase 5 gates.'
    }
    artifacts = $artifactRecords
}
$manifestJson = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText((Join-Path $resolvedReleaseDirectory 'release-manifest.json'), $manifestJson, [Text.UTF8Encoding]::new($false))

Write-Host "Preview checkpoint created: $resolvedReleaseDirectory"
Write-Host "Release $($release.ReleaseTag); desktop $($executableVersion.ProductVersion); MSIX $($release.MsixVersion); source-only public distribution."
