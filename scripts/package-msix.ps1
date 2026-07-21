[CmdletBinding()]
param(
    [string]$Version = '0.1.0.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root 'artifacts\publish\win-x64'
$staging = Join-Path $root 'artifacts\msix\staging'
$output = Join-Path $root "artifacts\msix\NextRDP-$Version-x64.msix"
$kitRoot = 'C:\Program Files (x86)\Windows Kits\10\bin'
$kitBin = Get-ChildItem -LiteralPath $kitRoot -Directory -ErrorAction SilentlyContinue |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\signtool.exe')) } |
    Sort-Object { [version]$_.Name } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($kitBin)) { throw 'Windows SDK x64 packaging tools were not found.' }
$makeAppx = Join-Path $kitBin 'x64\makeappx.exe'
$signTool = Join-Path $kitBin 'x64\signtool.exe'

if (-not (Test-Path -LiteralPath (Join-Path $publish 'NextRdp.Desktop.exe'))) { & (Join-Path $PSScriptRoot 'publish-windows.ps1') }
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $staging -Recurse

$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10" xmlns:uap10="http://schemas.microsoft.com/appx/manifest/uap/windows10/10" xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities" IgnorableNamespaces="uap uap10 rescap">
  <Identity Name="NextRDP.Client" Publisher="CN=NextRDP Development" Version="$Version" ProcessorArchitecture="x64" />
  <Properties><DisplayName>NextRDP</DisplayName><PublisherDisplayName>NextRDP Development</PublisherDisplayName><Logo>Assets\StoreLogo.png</Logo></Properties>
  <Resources><Resource Language="en-us" /></Resources>
  <Dependencies><TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.22000.0" MaxVersionTested="10.0.26100.0" /></Dependencies>
  <Applications>
    <Application Id="App" Executable="NextRdp.Desktop.exe" uap10:RuntimeBehavior="packagedClassicApp" uap10:TrustLevel="mediumIL">
      <uap:VisualElements DisplayName="NextRDP" Description="Secure local remote desktop manager" BackgroundColor="#0B1018" Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
  <Capabilities><rescap:Capability Name="runFullTrust" /><Capability Name="internetClient" /><Capability Name="privateNetworkClientServer" /></Capabilities>
</Package>
"@
$manifest | Set-Content -LiteralPath (Join-Path $staging 'AppxManifest.xml') -Encoding utf8

$assets = Join-Path $staging 'Assets'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
foreach ($assetName in @('StoreLogo.png', 'Square150x150Logo.png', 'Square44x44Logo.png')) {
    $sourceAsset = Join-Path $root "src\NextRdp.Desktop\Assets\$assetName"
    if (-not (Test-Path -LiteralPath $sourceAsset)) { throw "Package logo asset not found: $sourceAsset" }
    Copy-Item -LiteralPath $sourceAsset -Destination (Join-Path $assets $assetName) -Force
}

if (Test-Path -LiteralPath $output) { Remove-Item -LiteralPath $output -Force }
& $makeAppx pack /d $staging /p $output /o
if ($LASTEXITCODE -ne 0) { throw "MSIX packaging failed with exit code $LASTEXITCODE." }

$certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=NextRDP Development' | Sort-Object NotAfter -Descending | Select-Object -First 1
if ($null -eq $certificate -or $certificate.NotAfter -lt (Get-Date).AddDays(30)) {
    $certificate = New-SelfSignedCertificate -Type Custom -Subject 'CN=NextRDP Development' -KeyUsage DigitalSignature -FriendlyName 'NextRDP test signing' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') -NotAfter (Get-Date).AddYears(1)
}
& $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint /s My $output
if ($LASTEXITCODE -ne 0) { throw "MSIX signing failed with exit code $LASTEXITCODE." }

$signature = Get-AuthenticodeSignature -LiteralPath $output
$expectedThumbprint = $certificate.Thumbprint.Replace(' ', '').ToUpperInvariant()
$actualThumbprint = if ($null -eq $signature.SignerCertificate) { '' } else { $signature.SignerCertificate.Thumbprint.Replace(' ', '').ToUpperInvariant() }
if ($actualThumbprint -ne $expectedThumbprint) {
    throw "Signed MSIX verification failed: expected signer $expectedThumbprint but found $actualThumbprint."
}

$isExpectedUntrustedTestRoot =
    $signature.Status -eq 'UnknownError' -and
    $signature.StatusMessage -match '(?i)certificate chain.*root certificate.*not trusted|root certificate.*not trusted'
if ($signature.Status -ne 'Valid' -and -not $isExpectedUntrustedTestRoot) {
    throw "Signed MSIX verification failed: $($signature.StatusMessage)"
}
if ($isExpectedUntrustedTestRoot) {
    Write-Warning 'The MSIX signature matches the generated test certificate, but its self-signed root is not trusted on this machine. Import the exported certificate only on test systems before installation.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($output)
try
{
    $manifestEntry = $archive.GetEntry('AppxManifest.xml')
    if ($null -eq $manifestEntry) { throw 'The signed MSIX does not contain AppxManifest.xml.' }
    $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
    try { [xml]$packagedManifest = $manifestReader.ReadToEnd() } finally { $manifestReader.Dispose() }
    $payloadNames = @($archive.Entries | ForEach-Object FullName)
}
finally
{
    $archive.Dispose()
}

$packagedIdentity = $packagedManifest.Package.Identity
if ($packagedIdentity.Name -ne 'NextRDP.Client' -or $packagedIdentity.Version -ne $Version -or $packagedIdentity.ProcessorArchitecture -ne 'x64')
{
    throw "Unexpected packaged identity: $($packagedIdentity.Name) $($packagedIdentity.Version) $($packagedIdentity.ProcessorArchitecture)."
}
$debugPayload = @($payloadNames | Where-Object { $_ -match '(?i)\.pdb$' })
if ($debugPayload.Count -gt 0) { throw "Debug symbols were included in the MSIX: $($debugPayload[0])." }
$sensitivePayload = @($payloadNames | Where-Object { $_ -match '(?i)(password|credential|secret|token|\.rdp$|\.rdg$|nextrdp\.db$)' })
if ($sensitivePayload.Count -gt 0) { throw "Potentially sensitive payload was included in the MSIX: $($sensitivePayload[0])." }

$cerPath = Join-Path $root 'artifacts\msix\NextRDP-TestSigning.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null
$packageHash = Get-FileHash -LiteralPath $output -Algorithm SHA256
"$($packageHash.Hash) *$([IO.Path]::GetFileName($packageHash.Path))" | Set-Content -LiteralPath (Join-Path $root 'artifacts\msix\SHA256SUMS.txt') -Encoding ascii

Write-Host "Verified test-signed MSIX: $output"
Write-Host "SHA-256: $($packageHash.Hash); payload entries: $($payloadNames.Count); PDB/sensitive-name entries: 0."
