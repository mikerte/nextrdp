[CmdletBinding()]
param(
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$iconPath = Join-Path $root 'src\NextRdp.Desktop\Assets\nextrdp.ico'
$projectPath = Join-Path $root 'src\NextRdp.Desktop\NextRdp.Desktop.csproj'
$appXamlPath = Join-Path $root 'src\NextRdp.Desktop\App.xaml'
$expectedSizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)

if (-not (Test-Path -LiteralPath $iconPath)) { throw "Application icon not found: $iconPath" }
$bytes = [IO.File]::ReadAllBytes($iconPath)
if ($bytes.Length -lt 6 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or [BitConverter]::ToUInt16($bytes, 2) -ne 1) {
    throw 'The application icon does not have a valid ICO header.'
}

$count = [BitConverter]::ToUInt16($bytes, 4)
if ($count -ne $expectedSizes.Count) { throw "The application icon contains $count frame(s); expected $($expectedSizes.Count)." }
$actualSizes = [Collections.Generic.List[int]]::new()
for ($index = 0; $index -lt $count; $index++) {
    $entryOffset = 6 + ($index * 16)
    if ($entryOffset + 16 -gt $bytes.Length) { throw "Icon directory entry $index exceeds the file boundary." }
    $width = if ($bytes[$entryOffset] -eq 0) { 256 } else { [int]$bytes[$entryOffset] }
    $height = if ($bytes[$entryOffset + 1] -eq 0) { 256 } else { [int]$bytes[$entryOffset + 1] }
    $bitsPerPixel = [BitConverter]::ToUInt16($bytes, $entryOffset + 6)
    $payloadLength = [BitConverter]::ToUInt32($bytes, $entryOffset + 8)
    $payloadOffset = [BitConverter]::ToUInt32($bytes, $entryOffset + 12)
    if ($width -ne $height -or $bitsPerPixel -ne 32) { throw "Icon frame $index is not a square 32-bit image." }
    if ($payloadOffset + $payloadLength -gt $bytes.Length) { throw "Icon frame $index exceeds the file boundary." }
    if ([BitConverter]::ToUInt32($bytes, [int]$payloadOffset) -ne 40) {
        throw "Icon frame ${width}x$height is not stored as a Windows-compatible bitmap frame."
    }
    $dibWidth = [BitConverter]::ToInt32($bytes, [int]$payloadOffset + 4)
    $dibHeight = [BitConverter]::ToInt32($bytes, [int]$payloadOffset + 8)
    if ($dibWidth -ne $width -or $dibHeight -ne ($height * 2)) { throw "Icon frame ${width}x$height has invalid bitmap dimensions." }
    $actualSizes.Add($width)
}

if (Compare-Object $expectedSizes ($actualSizes | Sort-Object)) { throw 'The application icon is missing one or more required Windows sizes.' }
$project = Get-Content -LiteralPath $projectPath -Raw
if ($project -notmatch '<ApplicationIcon>Assets\\nextrdp\.ico</ApplicationIcon>') { throw 'The desktop executable does not declare the NextRDP application icon.' }
$appXaml = Get-Content -LiteralPath $appXamlPath -Raw
if ($appXaml -notmatch '<Setter Property="Icon" Value="[^\"]*Assets/nextrdp-logo\.png"\s*/>') { throw 'WPF windows do not inherit the shared NextRDP window icon.' }

Add-Type -AssemblyName System.Drawing
function Test-DecodedIcon([Drawing.Icon]$Icon, [string]$Description) {
    $bitmap = $Icon.ToBitmap()
    try {
        $visible = 0
        $transparent = 0
        for ($y = 0; $y -lt $bitmap.Height; $y++) {
            for ($x = 0; $x -lt $bitmap.Width; $x++) {
                if ($bitmap.GetPixel($x, $y).A -eq 0) { $transparent++ } else { $visible++ }
            }
        }
        $pixels = $bitmap.Width * $bitmap.Height
        if ($visible -lt [Math]::Ceiling($pixels * 0.1) -or $transparent -lt [Math]::Ceiling($pixels * 0.1)) {
            throw "$Description does not contain the expected visible logo and transparent background."
        }
    }
    finally {
        $bitmap.Dispose()
    }
}

foreach ($size in @(16, 32, 48, 256)) {
    $icon = [Drawing.Icon]::new($iconPath, $size, $size)
    try { Test-DecodedIcon $icon "The ${size}x$size application icon" }
    finally { $icon.Dispose() }
}

if (-not [string]::IsNullOrWhiteSpace($ExecutablePath)) {
    if (-not (Test-Path -LiteralPath $ExecutablePath)) { throw "Desktop executable not found: $ExecutablePath" }
    $embeddedIcon = [Drawing.Icon]::ExtractAssociatedIcon((Resolve-Path -LiteralPath $ExecutablePath).Path)
    if ($null -eq $embeddedIcon) { throw 'The desktop executable does not contain an application icon.' }
    try { Test-DecodedIcon $embeddedIcon 'The desktop executable icon' }
    finally { $embeddedIcon.Dispose() }
}

Write-Host "Brand asset checks passed: $count compatible icon frames, shared WPF window icon, and embedded executable icon."
