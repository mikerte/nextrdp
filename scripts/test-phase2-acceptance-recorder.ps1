[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$templatePath = Join-Path $projectRoot 'samples\phase2-acceptance-results.template.json'
$recorderPath = Join-Path $PSScriptRoot 'record-phase2-acceptance.ps1'
$temporaryDirectory = Join-Path ([IO.Path]::GetTempPath()) "nextrdp-phase2-recorder-$([Guid]::NewGuid().ToString('N'))"

function Copy-TemplateDocument {
    (Get-Content -LiteralPath $templatePath -Raw | ConvertFrom-Json) | ConvertTo-Json -Depth 10 | ConvertFrom-Json
}

function Assert-Rejected([object]$Document, [string]$CaseName) {
    $path = Join-Path $temporaryDirectory "$CaseName.json"
    [IO.File]::WriteAllText($path, ($Document | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $rejected = $false
    try { $null = & $recorderPath -ResultsPath $path -ValidateOnly }
    catch { $rejected = $true }
    if (-not $rejected) { throw "Phase 2 recorder negative case '$CaseName' was accepted unexpectedly." }
}

try {
    [IO.Directory]::CreateDirectory($temporaryDirectory) | Out-Null
    $null = & $recorderPath -ResultsPath $templatePath -ValidateOnly

    $document = Copy-TemplateDocument
    $document | Add-Member -NotePropertyName notes -NotePropertyValue 'unexpected'
    Assert-Rejected $document 'extra-root-property'

    $document = Copy-TemplateDocument
    $document.checks[1].id = $document.checks[0].id
    Assert-Rejected $document 'duplicate-id'

    $document = Copy-TemplateDocument
    $document.checks[0].PSObject.Properties.Remove('result')
    Assert-Rejected $document 'missing-check-property'

    $document = Copy-TemplateDocument
    $document.checks[0].result = 'assumed-pass'
    Assert-Rejected $document 'invalid-result'

    $document = Copy-TemplateDocument
    $document.privacyBoundaryAcknowledged = 'true'
    Assert-Rejected $document 'non-boolean-acknowledgement'

    . (Join-Path $PSScriptRoot 'release-metadata.ps1')
    $release = Get-NextRdpReleaseMetadata -ProjectRoot $projectRoot
    $packagePath = Join-Path $projectRoot "artifacts\msix\NextRDP-$($release.MsixVersion)-x64.msix"
    if (Test-Path -LiteralPath $packagePath -PathType Leaf) {
        $document = Copy-TemplateDocument
        $document.privacyBoundaryAcknowledged = $true
        $document.promptOnlyCredentialsConfirmed = $true
        $partialPath = Join-Path $temporaryDirectory 'partial-results.json'
        [IO.File]::WriteAllText($partialPath, ($document | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
        $null = & $recorderPath -ResultsPath $partialPath -PackagePath $packagePath -OutputDirectory $temporaryDirectory -AllowIncomplete
        $recordPath = Get-ChildItem -LiteralPath $temporaryDirectory -Filter 'NextRDP-Phase2-Target-Acceptance-*.json' | Select-Object -First 1
        if ($null -eq $recordPath) { throw 'Phase 2 recorder did not create an incomplete evidence record.' }
        $record = Get-Content -LiteralPath $recordPath.FullName -Raw | ConvertFrom-Json
        if ($record.overallResult -ne 'incomplete' -or @($record.checks).Count -ne 28 -or @($record.groups).Count -ne 5) {
            throw 'Phase 2 incomplete evidence record has an unexpected result or shape.'
        }
        if ($record.package.sha256 -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) {
            throw 'Phase 2 incomplete evidence record is not bound to the current package.'
        }
        if ([int]$record.source.fileCount -lt 1 -or ([string]$record.source.sha256).Length -ne 64) {
            throw 'Phase 2 incomplete evidence record is not bound to a source fingerprint.'
        }
    }

    Write-Host 'Phase 2 target-acceptance recorder tests passed: canonical template, 5 fail-closed schema cases, and package-bound incomplete record when the signed package is present.'
}
finally {
    if (Test-Path -LiteralPath $temporaryDirectory) { Remove-Item -LiteralPath $temporaryDirectory -Recurse -Force }
}
