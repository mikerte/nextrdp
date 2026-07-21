Set-StrictMode -Version Latest

function Get-NextRdpReleaseMetadata {
    [CmdletBinding()]
    param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot))

    $metadataPath = Join-Path $ProjectRoot 'eng\Release.props'
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
        throw "Release metadata was not found: $metadataPath"
    }

    [xml]$document = Get-Content -LiteralPath $metadataPath -Raw -Encoding utf8
    $properties = $document.Project.PropertyGroup
    if ($null -eq $properties) { throw 'Release.props does not contain a PropertyGroup.' }

    $productVersion = [string]$properties.NextRdpProductVersion
    $releaseVersion = [string]$properties.NextRdpReleaseVersion
    $msixVersionText = [string]$properties.NextRdpMsixVersion
    $releaseTag = [string]$properties.NextRdpReleaseTag
    $releaseChannel = [string]$properties.NextRdpReleaseChannel
    $packageIdentity = [string]$properties.NextRdpPackageIdentity
    $packagePublisher = [string]$properties.NextRdpPackagePublisher

    if ($productVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$') {
        throw "Product version '$productVersion' is not a three-part semantic version."
    }
    if ($releaseVersion -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
        throw "Release version '$releaseVersion' is not a supported semantic version."
    }
    if (-not $releaseVersion.StartsWith("$productVersion-", [StringComparison]::Ordinal)) {
        throw "Preview release version '$releaseVersion' must extend product version '$productVersion'."
    }

    $msixVersion = $null
    if (-not [Version]::TryParse($msixVersionText, [ref]$msixVersion) -or $msixVersion.ToString(4) -ne $msixVersionText) {
        throw "MSIX version '$msixVersionText' must be a numeric four-part version."
    }
    foreach ($part in @($msixVersion.Major, $msixVersion.Minor, $msixVersion.Build, $msixVersion.Revision)) {
        if ($part -lt 0 -or $part -gt 65535) { throw "MSIX version '$msixVersionText' contains a component outside 0-65535." }
    }
    if ("$($msixVersion.Major).$($msixVersion.Minor).$($msixVersion.Build)" -ne $productVersion) {
        throw "MSIX version '$msixVersionText' does not match product version '$productVersion'."
    }
    if ($releaseTag -ne "v$releaseVersion") { throw "Release tag '$releaseTag' must be v$releaseVersion." }
    if ($releaseChannel -ne 'preview') { throw "Release channel '$releaseChannel' is not supported by the preview workflow." }
    if ([string]::IsNullOrWhiteSpace($packageIdentity) -or [string]::IsNullOrWhiteSpace($packagePublisher)) {
        throw 'Package identity and publisher must be present in Release.props.'
    }

    [pscustomobject]@{
        ProductVersion = $productVersion
        ReleaseVersion = $releaseVersion
        MsixVersion = $msixVersionText
        ReleaseTag = $releaseTag
        ReleaseChannel = $releaseChannel
        PackageIdentity = $packageIdentity
        PackagePublisher = $packagePublisher
        MetadataPath = $metadataPath
    }
}
