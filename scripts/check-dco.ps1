[CmdletBinding()]
param(
    [string]$BaseSha = '',
    [string]$HeadSha = 'HEAD'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$useRange = $false
if (-not [string]::IsNullOrWhiteSpace($BaseSha) -and $BaseSha -notmatch '^0+$') {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        git -C $root rev-parse --verify --quiet "$BaseSha`^{commit}" *> $null
        if ($LASTEXITCODE -eq 0) {
            git -C $root merge-base --is-ancestor $BaseSha $HeadSha *> $null
            $useRange = $LASTEXITCODE -eq 0
        }
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
}

if ($useRange) {
    $commits = @(git -C $root rev-list "$BaseSha..$HeadSha")
} else {
    $commits = @(git -C $root rev-list $HeadSha)
}
if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate commits for DCO validation.' }

$missing = @()
foreach ($commit in $commits) {
    if ([string]::IsNullOrWhiteSpace($commit)) { continue }
    $message = git -C $root show -s --format=%B $commit
    if ($LASTEXITCODE -ne 0) { throw "Unable to read commit $commit." }
    if (($message -join "`n") -notmatch '(?im)^Signed-off-by:\s+.+\s+<[^<>]+>\s*$') { $missing += $commit }
}

if ($missing.Count -gt 0) {
    throw "Developer Certificate of Origin sign-off is missing from commit(s): $($missing -join ', '). Use git commit --signoff."
}

Write-Host "DCO check passed for $($commits.Count) commit(s)."
