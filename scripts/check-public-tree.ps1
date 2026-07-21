[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$requiredFiles = @(
    'LICENSE', 'NOTICE', 'TRADEMARKS.md', 'THIRD-PARTY-NOTICES.md', 'README.md',
    'CONTRIBUTING.md', 'CODE_OF_CONDUCT.md', 'SECURITY.md', 'SUPPORT.md', 'CHANGELOG.md'
)

foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $requiredFile) -PathType Leaf)) {
        throw "Required public project file is missing: $requiredFile"
    }
}

$tracked = @(git -C $root ls-files)
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate Git files for the public-tree check.' }
$tracked = @($tracked | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
if ($tracked.Count -eq 0) {
    $tracked = @(Get-ChildItem -LiteralPath $root -Recurse -File |
        Where-Object { $_.FullName -notmatch '[\\/](\.git|bin|obj|artifacts|node_modules|dist|TestResults|coverage)[\\/]' } |
        ForEach-Object { $_.FullName.Substring($root.Length + 1).Replace('\', '/') })
}

$forbiddenPathPatterns = @(
    '(^|/)docs/gatheragents(/|$)',
    '(^|/)node_modules(/|$)',
    '(^|/)dist(/|$)',
    '(^|/)bin(/|$)',
    '(^|/)obj(/|$)',
    '(^|/)artifacts(/|$)',
    '(^|/)TestResults(/|$)',
    '(^|/)coverage(/|$)'
)
$forbiddenExtensions = @('.pfx', '.cer', '.snk', '.db', '.db-shm', '.db-wal', '.user', '.suo')

foreach ($relative in $tracked) {
    if ($relative -eq 'AGENTS.md') { throw 'AGENTS.md is internal and must not be tracked in the public repository.' }
    foreach ($pattern in $forbiddenPathPatterns) {
        if ($relative -match $pattern) { throw "Generated or internal path is tracked: $relative" }
    }
    if ([IO.Path]::GetExtension($relative) -in $forbiddenExtensions) { throw "Sensitive or machine-local file is tracked: $relative" }
    if ([IO.Path]::GetFileName($relative) -eq '.env') { throw '.env must never be tracked.' }
}

$textExtensions = @('.cs', '.xaml', '.json', '.xml', '.ps1', '.csproj', '.props', '.targets', '.md', '.csv', '.txt', '.yml', '.yaml', '.ts', '.tsx', '.js', '.mjs', '.sql', '.toml', '.html', '.css')
$originPattern = '(?i)hackathon|devpost|build\s+week|\bjudge(?:s|d|ment)?\b|codex|gpt-?5\.6|gatheragents|\bopenai\b'
$privateIpPattern = '\b(?:10\.(?:\d{1,3}\.){2}\d{1,3}|192\.168\.(?:\d{1,3}\.)\d{1,3}|172\.(?:1[6-9]|2\d|3[01])\.(?:\d{1,3}\.)\d{1,3})\b'
$credentialPatterns = @(
    '(?i)(password|passwd|pwd|access[_-]?token|client[_-]?secret)\s*[:=]\s*["''][^"'']{8,}["'']',
    '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
    '(?i)\b(?:https?|postgres(?:ql)?|mssql|sqlserver)://[^/\s:@]+:[^@\s/]{8,}@',
    '(?i)^\s*password\s+51\s*:\s*b\s*:\s*[0-9a-f]{16,}\s*$',
    '(?i)\b(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{20,}|AKIA[A-Z0-9]{16}|sk-[A-Za-z0-9_-]{20,})\b'
)
$allowedLegacyIdentityFiles = @('README.md', 'docs/install-desktop.md')

foreach ($relative in $tracked) {
    if ([IO.Path]::GetExtension($relative) -notin $textExtensions) { continue }
    if ($relative -eq 'scripts/check-public-tree.ps1') { continue }
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    $content = [IO.File]::ReadAllText($path)

    if ($content.Contains('NextRDP.Hackathon') -and $relative -notin $allowedLegacyIdentityFiles) {
        throw "Legacy package identity appears outside its migration documentation: $relative"
    }
    $originScan = $content.Replace('NextRDP.Hackathon', '')
    if ($originScan -match $originPattern) { throw "Internal project-origin terminology appears in: $relative" }
    if ($content -match $privateIpPattern) { throw "RFC 1918 address appears in public source: $relative" }
    if ($content -match '(?i)C:\\Users\\kerte|C:\\stuffs\\mydev') { throw "Machine-local absolute path appears in public source: $relative" }

    foreach ($pattern in $credentialPatterns) {
        $matches = [regex]::Matches($content, $pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
        foreach ($match in $matches) {
            if ($match.Value -match '\$\{[A-Z][A-Z0-9_]*(?::[^}]*)?\}') { continue }
            throw "Potential credential material appears in public source: $relative"
        }
    }
}

Write-Host "Public-tree check passed for $($tracked.Count) file(s): community files present; no internal/generated paths, project-origin terms, private addresses, machine-local paths, or credential patterns detected."
