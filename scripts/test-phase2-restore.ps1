param(
    [string]$SourceBaseUri = "http://127.0.0.1:5090",
    [string]$RestoreBaseUri = "http://127.0.0.1:5190",
    [Parameter(Mandatory = $true)][string]$PostgresPassword,
    [Parameter(Mandatory = $true)][string]$BackupPassphrase
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$projectName = "nextrdp-restore-test"
if ($projectName -ne "nextrdp-restore-test") { throw "Restore drill project name safety check failed." }
$composeFiles = @("-f", "compose.yaml", "-f", "compose.restore-test.yaml")
$artifact = Join-Path ([IO.Path]::GetTempPath()) "nextrdp-restore-drill-$([Guid]::NewGuid().ToString('N')).backup"
$previousPassword = $env:POSTGRES_PASSWORD

function Assert-RestoreDrill([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Phase 2 restore assertion failed: $Message" }
}

function New-SignedInSession([string]$BaseUri) {
    $null = Invoke-WebRequest -Uri "$BaseUri/auth/development-signin?returnUrl=%2F" -SessionVariable webSession -MaximumRedirection 5 -UseBasicParsing
    $csrf = Invoke-RestMethod -Uri "$BaseUri/auth/csrf" -WebSession $webSession -Method Get
    [pscustomobject]@{ Session = $webSession; Csrf = [string]$csrf.requestToken }
}

function Invoke-Json([string]$BaseUri, [Microsoft.PowerShell.Commands.WebRequestSession]$Session, [string]$Csrf, [string]$Method, [string]$Path, [object]$Body) {
    $parameters = @{ Uri = "$BaseUri$Path"; WebSession = $Session; Method = $Method; UseBasicParsing = $true; Headers = @{ "X-CSRF-TOKEN" = $Csrf } }
    if ($null -ne $Body) { $parameters.ContentType = "application/json"; $parameters.Body = $Body | ConvertTo-Json -Depth 30 -Compress }
    try {
        $response = Invoke-WebRequest @parameters
    }
    catch {
        $status = if ($null -ne $_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        $detail = ""
        if ($null -ne $_.Exception.Response) {
            $reader = [IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
            try { $detail = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        throw "Restore drill request $Method $Path failed with HTTP $status. $detail"
    }
    if ([string]::IsNullOrWhiteSpace([string]$response.Content)) { return $null }
    return $response.Content | ConvertFrom-Json
}

try {
    $source = New-SignedInSession $SourceBaseUri
    $organizations = Invoke-Json $SourceBaseUri $source.Session $source.Csrf GET "/api/v1/organizations" $null
    Assert-RestoreDrill ($organizations.Count -gt 0) "source deployment has no organization to back up"
    $organizationId = [Guid]::Empty
    $workspaceId = [Guid]::Empty
    foreach ($candidateOrganization in $organizations) {
        $candidateOrganizationId = [Guid]$candidateOrganization.id
        $candidateWorkspaces = Invoke-Json $SourceBaseUri $source.Session $source.Csrf GET "/api/v1/organizations/$candidateOrganizationId/workspaces" $null
        foreach ($candidateWorkspace in $candidateWorkspaces) {
            $candidateWorkspaceId = [Guid]$candidateWorkspace.id
            $candidateSync = Invoke-Json $SourceBaseUri $source.Session $source.Csrf POST "/api/v1/organizations/$candidateOrganizationId/workspaces/$candidateWorkspaceId/sync" @{
                cursor = $null; clientVersion = "0.2.0"; knownRevisions = @{}
            }
            if ($candidateSync.items.Count -gt 0) {
                $organizationId = $candidateOrganizationId
                $workspaceId = $candidateWorkspaceId
                break
            }
        }
        if ($organizationId -ne [Guid]::Empty) { break }
    }
    Assert-RestoreDrill ($organizationId -ne [Guid]::Empty) "source deployment has no workspace with a synchronization journal"
    $backupBody = @{ passphrase = $BackupPassphrase; includeAudit = $true } | ConvertTo-Json -Compress
    $backupResponse = Invoke-WebRequest -Uri "$SourceBaseUri/api/v1/organizations/$organizationId/backup" -Method Post -WebSession $source.Session -Headers @{ "X-CSRF-TOKEN" = $source.Csrf } -ContentType "application/json" -Body $backupBody -OutFile $artifact -PassThru -UseBasicParsing
    $actualHash = (Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash
    Assert-RestoreDrill ($actualHash -eq [string]$backupResponse.Headers["X-Content-SHA256"]) "backup integrity header does not match the artifact"

    $env:POSTGRES_PASSWORD = $PostgresPassword
    & docker compose -p $projectName @composeFiles up -d --build --wait
    if ($LASTEXITCODE -ne 0) { throw "Disposable restore deployment did not become healthy." }

    $target = New-SignedInSession $RestoreBaseUri
    $restore = Invoke-Json $RestoreBaseUri $target.Session $target.Csrf POST "/api/v1/administration/restore" @{
        contentBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($artifact))
        passphrase = $BackupPassphrase
    }
    Assert-RestoreDrill ($restore.integrityVerified -eq $true) "restore did not report authenticated integrity"
    Assert-RestoreDrill ($restore.organizations -eq 1) "restore did not create exactly one organization"

    $restoredOrganizations = Invoke-Json $RestoreBaseUri $target.Session $target.Csrf GET "/api/v1/organizations" $null
    $restoredOrganization = $restoredOrganizations | Where-Object { $_.id -eq $organizationId.ToString() } | Select-Object -First 1
    Assert-RestoreDrill ($null -ne $restoredOrganization) "restored organization is not authorized for the recovery identity"
    $workspaces = Invoke-Json $RestoreBaseUri $target.Session $target.Csrf GET "/api/v1/organizations/$organizationId/workspaces" $null
    Assert-RestoreDrill ($workspaces.Count -gt 0) "restored organization has no workspace"
    Assert-RestoreDrill ($workspaces.id -contains $workspaceId.ToString()) "the selected source workspace was not restored"
    $sync = Invoke-Json $RestoreBaseUri $target.Session $target.Csrf POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/sync" @{ cursor = $null; clientVersion = "0.2.0"; knownRevisions = @{} }
    Assert-RestoreDrill (-not [string]::IsNullOrWhiteSpace([string]$sync.nextCursor)) "post-restore synchronization did not return a protected cursor"
    Assert-RestoreDrill ($sync.items.Count -gt 0) "post-restore synchronization did not return the restored inventory journal"
    $serialized = $sync | ConvertTo-Json -Depth 30 -Compress
    Assert-RestoreDrill ($serialized -notmatch '(?i)password|clientSecret|credentialReference') "post-restore synchronization exposed credential-shaped data"

    [pscustomobject]@{
        Status = "passed"
        OrganizationId = $organizationId
        WorkspaceId = $workspaceId
        RestoredConnections = $restore.connections
        RestoredConfigurations = $restore.configurations
        SyncItems = $sync.items.Count
        IntegrityVerified = $restore.integrityVerified
    } | ConvertTo-Json -Depth 4
}
catch {
    $failureMessage = $_.Exception.Message
    $diagnosticPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $serviceLogs = @(& docker compose -p $projectName @composeFiles logs --tail 160 control-plane 2>&1) -join "`n"
    }
    finally {
        $ErrorActionPreference = $diagnosticPreference
    }
    throw "Disposable restore drill failed: $failureMessage`n$serviceLogs"
}
finally {
    if (Get-Command docker -ErrorAction SilentlyContinue) {
        $env:POSTGRES_PASSWORD = $PostgresPassword
        $cleanupPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = "Continue"
            $null = @(& docker compose -p $projectName @composeFiles down --volumes --remove-orphans 2>&1)
        }
        finally {
            $ErrorActionPreference = $cleanupPreference
        }
    }
    if ($null -eq $previousPassword) { Remove-Item Env:POSTGRES_PASSWORD -ErrorAction SilentlyContinue } else { $env:POSTGRES_PASSWORD = $previousPassword }
    Remove-Item -LiteralPath $artifact -Force -ErrorAction SilentlyContinue
}
