param(
    [string]$BaseUri = "http://127.0.0.1:5090",
    [Parameter(Mandatory = $true)]
    [string]$BackupPassphrase
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Assert-Phase2 {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw "Phase 2 smoke assertion failed: $Message" }
}

function Get-Phase2HttpStatus {
    param([scriptblock]$Operation)
    try {
        $response = & $Operation
        return [int]$response.StatusCode
    }
    catch {
        if ($null -eq $_.Exception.Response) { throw }
        return [int]$_.Exception.Response.StatusCode
    }
}

function Invoke-Phase2Json {
    param(
        [ValidateSet("GET", "POST", "PUT", "DELETE")]
        [string]$Method,
        [string]$Path,
        [object]$Body,
        [hashtable]$AdditionalHeaders = @{}
    )

    $headers = @{}
    if ($script:csrfToken) { $headers["X-CSRF-TOKEN"] = $script:csrfToken }
    foreach ($entry in $AdditionalHeaders.GetEnumerator()) { $headers[$entry.Key] = $entry.Value }

    $parameters = @{
        Uri = "$BaseUri$Path"
        Method = $Method
        WebSession = $script:session
        Headers = $headers
        UseBasicParsing = $true
    }
    if ($null -ne $Body) {
        $parameters.ContentType = "application/json"
        $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }

    try {
        $response = Invoke-WebRequest @parameters
    } catch {
        $statusCode = [int]$_.Exception.Response.StatusCode
        $reader = [System.IO.StreamReader]::new($_.Exception.Response.GetResponseStream())
        try { $errorContent = $reader.ReadToEnd() } finally { $reader.Dispose() }
        throw "Phase 2 API request failed: $Method $Path returned ${statusCode}: $errorContent"
    }
    if ($response.StatusCode -lt 200 -or $response.StatusCode -ge 300) {
        throw "Phase 2 API request failed: $Method $Path returned $($response.StatusCode): $($response.Content)"
    }

    $content = if ($response.Content -is [byte[]]) {
        [System.Text.Encoding]::UTF8.GetString($response.Content)
    } else {
        [string]$response.Content
    }
    $data = if ([string]::IsNullOrWhiteSpace($content)) { $null } else { $content | ConvertFrom-Json }
    [pscustomobject]@{ StatusCode = $response.StatusCode; Headers = $response.Headers; Data = $data }
}

$signIn = Invoke-WebRequest -Uri "$BaseUri/auth/development-signin?returnUrl=%2F" -SessionVariable session -MaximumRedirection 5 -UseBasicParsing
Assert-Phase2 ($signIn.StatusCode -eq 200) "development sign-in did not return to the administration console"
$me = Invoke-Phase2Json GET "/auth/me" $null
Assert-Phase2 ($me.Data.mfaSatisfied -eq $true) "authenticated development identity did not satisfy the local MFA test claim"

$csrf = Invoke-Phase2Json GET "/auth/csrf" $null
$script:csrfToken = [string]$csrf.Data.requestToken
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace($script:csrfToken)) "CSRF token was not issued"

$suffix = [Guid]::NewGuid().ToString("N").Substring(0, 10)
$organization = (Invoke-Phase2Json POST "/api/v1/organizations" @{ name = "Phase 2 Smoke $suffix"; slug = "phase2-$suffix" }).Data
$organizationId = [Guid]$organization.id
$workspace = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces" @{ name = "Operations"; slug = "operations"; description = "Phase 2 acceptance workspace" }).Data
$workspaceId = [Guid]$workspace.id
$otherWorkspace = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces" @{ name = "Restricted"; slug = "restricted"; description = "Cross-workspace deny test" }).Data
$otherWorkspaceId = [Guid]$otherWorkspace.id
$otherOrganization = (Invoke-Phase2Json POST "/api/v1/organizations" @{ name = "Phase 2 Boundary $suffix"; slug = "phase2-boundary-$suffix" }).Data
$otherOrganizationId = [Guid]$otherOrganization.id
$otherOrganizationWorkspace = (Invoke-Phase2Json POST "/api/v1/organizations/$otherOrganizationId/workspaces" @{ name = "Boundary"; slug = "boundary"; description = "Cross-organization deny test" }).Data
$otherOrganizationWorkspaceId = [Guid]$otherOrganizationWorkspace.id

$users = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/users" $null).Data
Assert-Phase2 ($users.Count -ge 1) "the federated user was not provisioned"
$userId = [Guid]$users[0].id
$membership = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/memberships" @{
    userId = $userId
    workspaceId = $workspaceId
    role = "Operator"
    status = "Active"
    sourceGroup = "phase2-smoke"
    expectedRevision = $null
}).Data
$memberships = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/memberships?workspaceId=$workspaceId" $null).Data
Assert-Phase2 ($memberships.Count -eq 1 -and $memberships[0].id -eq $membership.id) "workspace membership did not round-trip"
$groupMapping = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/group-role-mappings" @{
    workspaceId = $workspaceId; groupClaim = "phase2-smoke-group"; role = "Operator"; enabled = $true; expectedRevision = $null
}).Data
$groupMappings = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/group-role-mappings" $null).Data
Assert-Phase2 ($groupMappings.Count -eq 1 -and $groupMappings[0].id -eq $groupMapping.id) "group-to-role mapping did not round-trip"

$folder = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/folders" @{
    parentId = $null
    name = "Production"
    sortOrder = 10
    expectedRevision = $null
}).Data
$folderId = [Guid]$folder.id

$connectionBody = @{
    folderId = $folderId
    visibility = "Shared"
    name = "Smoke RDP"
    host = "192.0.2.10"
    port = 3389
    usernameHint = "Administrator"
    environment = "Production"
    notes = "Secret-free Phase 2 smoke profile"
    tags = @("smoke", "production")
    metadata = @{
        businessUnit = "IT"; applicationOwner = "Platform"; serviceOwner = "Operations"; criticality = "High"
        geography = "EU"; dataClassification = "Internal"; maintenanceWindow = "Sunday 02:00 UTC"
        supportGroup = "Infrastructure"; escalationContact = "On-call"; assetId = "asset-$suffix"; cmdbId = "cmdb-$suffix"
        operatingSystem = "Windows Server"; monitoringUrl = "https://monitoring.invalid/$suffix"
        documentationUrl = "https://docs.invalid/$suffix"; ticketingUrl = "https://tickets.invalid/$suffix"
    }
    settings = @{
        kind = "Desktop"; requireNetworkLevelAuthentication = $true; administrativeSession = $false; brokerLoadBalanceInfo = ""
        gatewayUsage = "Detect"; gatewayHost = "gateway.invalid"; gatewayPort = 443; remoteAppProgram = ""
        remoteAppWorkingDirectory = ""; remoteAppArguments = ""; displayMode = "Embedded"; selectedMonitorIds = @()
        desktopWidth = 1920; desktopHeight = 1080; colorDepth = 32; performancePreset = "Auto"
        showConnectionQuality = $true; thumbnailMode = "PrivacyLocked"; shortcutRouting = "RemoteWhenFocused"
        allowSecureAttentionSequence = $false; redirectMicrophone = $false; redirectCamera = $false; redirectComPorts = $false
        redirectLptPorts = $false; redirectUsbDevices = $false; redirectSmartCards = $true; redirectPrinters = $true
        redirectDrives = $true; redirectAudio = $true; clipboardDirection = "Bidirectional"; fileTransferDirection = "ClientToRemote"
        sessionTimeoutMinutes = 30; reconnectAttempts = 3
    }
    expectedRevision = $null
}
$connection = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections" $connectionBody).Data
$connectionId = [Guid]$connection.id
$searchTerms = @(
    "Smoke", "192.0.2.10", "production", "IT", "Platform", "Operations", "High", "EU", "Internal",
    "Sunday 02:00 UTC", "Infrastructure", "On-call", "asset-$suffix", "cmdb-$suffix", "Windows Server",
    "https://monitoring.invalid/$suffix", "https://docs.invalid/$suffix", "https://tickets.invalid/$suffix"
)
foreach ($searchTerm in $searchTerms) {
    $encodedSearch = [Uri]::EscapeDataString($searchTerm)
    $search = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections?search=$encodedSearch&pageSize=20" $null).Data
    Assert-Phase2 ($search.items.Count -eq 1 -and $search.items[0].id -eq $connection.id) "inventory search did not match '$searchTerm' across the operational metadata contract"
}

$draft = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/configuration/drafts?workspaceId=$workspaceId" @{
    scopeType = "Workspace"
    scopeId = $workspaceId
    baseVersion = 0
    values = @{ "session.timeoutMinutes" = 30; "rdp.requireNla" = $true }
    lockedFields = @("rdp.requireNla")
    summary = "Phase 2 smoke policy"
    expectedRevision = $null
}).Data
$published = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/configuration/drafts/$($draft.id)/publish?workspaceId=$workspaceId" @{}).Data
$effective = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/workspaces/$workspaceId/configuration/effective?folderId=$folderId&connectionId=$connectionId" $null).Data
Assert-Phase2 ($effective.fields.'session.timeoutMinutes'.jsonValue -eq "30") "published configuration was not present in the effective policy"
Assert-Phase2 ($effective.fields.'rdp.requireNla'.locked -eq $true) "published configuration lock was not preserved"

$sync = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/sync" @{
    cursor = $null; clientVersion = "2.0.0"; knownRevisions = @{}
}).Data
Assert-Phase2 ($sync.items.Count -ge 3 -and -not [string]::IsNullOrWhiteSpace($sync.nextCursor)) "initial desktop sync did not return workspace resources"

$export = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/workspaces/$workspaceId/export" $null).Data
$exportJson = $export | ConvertTo-Json -Depth 20 -Compress
Assert-Phase2 ($export.connections.Count -eq 1) "secret-free export did not include the connection"
Assert-Phase2 ($exportJson -notmatch '(?i)password|clientSecret|credentialReference') "export exposed a credential-shaped field"

$dynamicGroup = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/dynamic-groups" @{
    name = "Production tag"; rules = @(@{ field = "tag"; operator = "equals"; value = "production" }); expectedRevision = $null
}).Data
Assert-Phase2 ($dynamicGroup.matchingConnectionIds -contains $connection.id) "dynamic group did not deterministically match metadata"
$template = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connection-templates" @{
    name = "Smoke template"; description = "Secret-free safe defaults"
    defaults = @{ environment = $connection.environment; notes = $connection.notes; tags = $connection.tags; metadata = $connection.metadata; settings = $connection.settings }
    expectedRevision = $null
}).Data
$templated = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections/from-template" @{
    templateId = $template.id; folderId = $folderId; visibility = "Personal"; name = "Templated RDP"; host = "192.0.2.11"; port = 3389; usernameHint = "DOMAIN\operator"
}).Data
$copied = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections/$connectionId/copy" @{
    name = "Smoke RDP copy"; folderId = $folderId; visibility = "Personal"
}).Data
$workspaceState = (Invoke-Phase2Json PUT "/api/v1/organizations/$organizationId/workspaces/$workspaceId/me/workspace-state" @{
    pinnedFolderIds = @($folderId)
    layout = @{ navigationWidth = 320; inventorySort = "name"; theme = "system"; reopenLastWorkspace = $true }
    savedWorkspaces = @(@{ id = [Guid]::NewGuid(); name = "Smoke workspace"; connectionIds = @($connectionId, [Guid]$templated.id); displayMode = "Embedded" })
    tabGroups = @(@{ id = [Guid]::NewGuid(); name = "Production"; color = "#5B8DEF"; connectionIds = @($connectionId) })
    expectedRevision = $null
}).Data
Assert-Phase2 ($workspaceState.pinnedFolderIds -contains $folder.id -and $workspaceState.savedWorkspaces.Count -eq 1) "personal workspace state did not persist"
$bulkPreview = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/bulk/connect-preview" @{
    connectionIds = @($connectionId, [Guid]$templated.id, [Guid]$copied.id)
}).Data
Assert-Phase2 ($bulkPreview.allowedConnectionIds.Count -eq 3 -and $bulkPreview.maximumConnections -ge 3) "bulk-connect impact preview did not authorize the bounded selection"
$bulkKey = "smoke-bulk-$suffix"
$bulkMetadata = @{
    businessUnit = "Bulk IT"; applicationOwner = "Bulk Platform"; serviceOwner = "Bulk Operations"; criticality = "Critical"
    geography = "HU"; dataClassification = "Restricted"; maintenanceWindow = "Saturday 01:00 UTC"
    supportGroup = "Bulk Support"; escalationContact = "Bulk On-call"; assetId = "bulk-asset-$suffix"; cmdbId = "bulk-cmdb-$suffix"
    operatingSystem = "Windows Server 2025"; monitoringUrl = "https://monitoring.invalid/bulk-$suffix"
    documentationUrl = "https://docs.invalid/bulk-$suffix"; ticketingUrl = "https://tickets.invalid/bulk-$suffix"
}
$bulkBody = @{ connectionIds = @($connectionId, [Guid]$templated.id); environment = $null; folderId = $null; clearFolder = $false; addTags = @("bulk-smoke"); removeTags = @(); applicationOwner = $null; criticality = $null; metadata = $bulkMetadata }
$bulkFirst = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/bulk/edit-connections" $bulkBody @{ "Idempotency-Key" = $bulkKey }).Data
$bulkReplay = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/bulk/edit-connections" $bulkBody @{ "Idempotency-Key" = $bulkKey }).Data
Assert-Phase2 ($bulkFirst.replayed -eq $false -and $bulkReplay.replayed -eq $true) "bulk edit did not replay idempotently"
$bulkUpdated = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections/$connectionId" $null).Data
foreach ($property in $bulkMetadata.Keys) {
    Assert-Phase2 ($bulkUpdated.metadata.PSObject.Properties[$property].Value -eq $bulkMetadata[$property]) "bulk edit did not persist metadata field '$property'"
}
$gatewayDiagnostic = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/workspaces/$workspaceId/gateway/diagnostics" @{
    host = "127.0.0.1"; port = 9; requireTls = $false; expectedServerName = "127.0.0.1"
}).Data
Assert-Phase2 ($gatewayDiagnostic.dnsSucceeded -eq $true -and -not [string]::IsNullOrWhiteSpace([string]$gatewayDiagnostic.outcome)) "gateway diagnostic did not return an actionable bounded result"

$serviceAccount = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/service-accounts" @{
    name = "Smoke automation"
    workspaceId = $workspaceId
    scopes = @("inventory.read", "configuration.read", "audit.read")
    expiresAtUtc = [DateTimeOffset]::UtcNow.AddHours(1).ToString("o")
}).Data
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$serviceAccount.clientSecret)) "service-account secret was not returned at creation"
$tokenResponse = Invoke-RestMethod -Method Post -Uri "$BaseUri/connect/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "client_credentials"
    client_id = $serviceAccount.clientId
    client_secret = $serviceAccount.clientSecret
    scope = "inventory.read"
}
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$tokenResponse.access_token)) "client-credentials token was not issued"
$serviceHeaders = @{ Authorization = "Bearer $($tokenResponse.access_token)" }
$serviceRead = Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections?pageSize=20" -Headers $serviceHeaders -UseBasicParsing
Assert-Phase2 ($serviceRead.StatusCode -eq 200) "scoped service account could not read workspace inventory"
$noAuthenticationStatus = Get-Phase2HttpStatus { Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections?pageSize=20" -UseBasicParsing }
Assert-Phase2 ($noAuthenticationStatus -eq 401) "unauthenticated API request did not fail with 401"
$crossWorkspaceStatus = Get-Phase2HttpStatus { Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$otherWorkspaceId/connections?pageSize=20" -Headers $serviceHeaders -UseBasicParsing }
Assert-Phase2 ($crossWorkspaceStatus -eq 403) "workspace-scoped service account crossed a workspace boundary"
$crossOrganizationStatus = Get-Phase2HttpStatus { Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$otherOrganizationId/workspaces/$otherOrganizationWorkspaceId/connections?pageSize=20" -Headers $serviceHeaders -UseBasicParsing }
Assert-Phase2 ($crossOrganizationStatus -eq 403) "service account crossed an organization boundary"
$scopeDeniedStatus = Get-Phase2HttpStatus {
    Invoke-WebRequest -Method Post -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections" -Headers $serviceHeaders -UseBasicParsing `
        -ContentType "application/json" -Body ($connectionBody | ConvertTo-Json -Depth 20 -Compress)
}
Assert-Phase2 ($scopeDeniedStatus -eq 403) "read-only service-account scope permitted an inventory write"
$malformedStatus = Get-Phase2HttpStatus {
    Invoke-WebRequest -Method Post -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections" -WebSession $script:session `
        -Headers @{ "X-CSRF-TOKEN" = $script:csrfToken } -UseBasicParsing -ContentType "application/json" -Body '{'
}
Assert-Phase2 ($malformedStatus -eq 400) "malformed JSON did not return a bounded 400 response"
$rotatedAccount = (Invoke-Phase2Json POST "/api/v1/organizations/$organizationId/service-accounts/$($serviceAccount.id)/rotate-secret" @{}).Data
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$rotatedAccount.clientSecret) -and $rotatedAccount.clientSecret -ne $serviceAccount.clientSecret) "service-account secret rotation did not issue a new one-time secret"
$rotatedToken = Invoke-RestMethod -Method Post -Uri "$BaseUri/connect/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "client_credentials"; client_id = $rotatedAccount.clientId; client_secret = $rotatedAccount.clientSecret; scope = "inventory.read"
}
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$rotatedToken.access_token)) "rotated service-account credential could not authenticate"

$audit = (Invoke-Phase2Json GET "/api/v1/organizations/$organizationId/audit?workspaceId=$workspaceId&limit=100" $null).Data
Assert-Phase2 ($audit.Count -ge 5) "workspace audit stream did not capture the acceptance operations"

$backupFile = Join-Path ([System.IO.Path]::GetTempPath()) "nextrdp-phase2-$suffix.backup"
try {
    $backupBody = @{ passphrase = $BackupPassphrase; includeAudit = $true } | ConvertTo-Json -Compress
    $backupResponse = Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$organizationId/backup" -Method POST -WebSession $script:session `
        -Headers @{ "X-CSRF-TOKEN" = $script:csrfToken } -ContentType "application/json" -Body $backupBody -OutFile $backupFile -PassThru -UseBasicParsing
    $actualHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $backupFile).Hash.ToLowerInvariant()
    $expectedHash = [string]$backupResponse.Headers["X-Content-SHA256"]
    Assert-Phase2 ($actualHash -eq $expectedHash.ToLowerInvariant()) "encrypted backup hash did not match its response integrity header"
    Assert-Phase2 ((Get-Item -LiteralPath $backupFile).Length -gt 256) "encrypted backup artifact was unexpectedly small"
} finally {
    Remove-Item -LiteralPath $backupFile -Force -ErrorAction SilentlyContinue
}

$random = [byte[]]::new(64)
$randomNumberGenerator = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $randomNumberGenerator.GetBytes($random) } finally { $randomNumberGenerator.Dispose() }
$verifier = [Convert]::ToBase64String($random).TrimEnd('=').Replace('+','-').Replace('/','_')
$sha256 = [Security.Cryptography.SHA256]::Create()
try { $challengeBytes = $sha256.ComputeHash([Text.Encoding]::ASCII.GetBytes($verifier)) } finally { $sha256.Dispose() }
$challenge = [Convert]::ToBase64String($challengeBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
$stateValue = [Guid]::NewGuid().ToString('N')
$nonceValue = [Guid]::NewGuid().ToString('N')
$redirectUri = "http://127.0.0.1:43876/callback"
$authorizeUri = "$BaseUri/connect/authorize?client_id=nextrdp-desktop&redirect_uri=$([Uri]::EscapeDataString($redirectUri))&response_type=code&scope=$([Uri]::EscapeDataString('openid profile offline_access inventory.read configuration.read'))&state=$stateValue&nonce=$nonceValue&code_challenge=$challenge&code_challenge_method=S256"
$authorizeRequest = [Net.HttpWebRequest]::Create($authorizeUri)
$authorizeRequest.AllowAutoRedirect = $false
$authorizeRequest.CookieContainer = $script:session.Cookies
$authorizeResponse = $authorizeRequest.GetResponse()
try { $callbackLocation = [Uri]$authorizeResponse.Headers['Location'] } finally { $authorizeResponse.Dispose() }
$callbackQuery = [Web.HttpUtility]::ParseQueryString($callbackLocation.Query)
Assert-Phase2 ($callbackQuery['state'] -eq $stateValue -and -not [string]::IsNullOrWhiteSpace($callbackQuery['code'])) "OIDC authorization code flow did not preserve state and issue a code"
$interactiveToken = Invoke-RestMethod -Method Post -Uri "$BaseUri/connect/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "authorization_code"; client_id = "nextrdp-desktop"; redirect_uri = $redirectUri; code = $callbackQuery['code']; code_verifier = $verifier
}
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$interactiveToken.access_token) -and -not [string]::IsNullOrWhiteSpace([string]$interactiveToken.refresh_token)) "OIDC PKCE exchange did not issue access and refresh tokens"
$interactiveHeaders = @{ Authorization = "Bearer $($interactiveToken.access_token)" }
$interactiveRead = Invoke-WebRequest -Uri "$BaseUri/api/v1/organizations/$organizationId/workspaces/$workspaceId/connections?pageSize=20" -Headers $interactiveHeaders -UseBasicParsing
Assert-Phase2 ($interactiveRead.StatusCode -eq 200) "interactive OIDC token could not read authorized inventory"
$refreshedToken = Invoke-RestMethod -Method Post -Uri "$BaseUri/connect/token" -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "refresh_token"; client_id = "nextrdp-desktop"; refresh_token = $interactiveToken.refresh_token
}
Assert-Phase2 (-not [string]::IsNullOrWhiteSpace([string]$refreshedToken.access_token)) "interactive refresh-token flow failed"
$logout = Invoke-WebRequest -Method Post -Uri "$BaseUri/auth/global-logout" -Headers @{ Authorization = "Bearer $($refreshedToken.access_token)" } -UseBasicParsing
Assert-Phase2 ($logout.StatusCode -eq 204) "global product logout did not complete"
$refreshRejected = $false
try {
    $null = Invoke-RestMethod -Method Post -Uri "$BaseUri/connect/token" -ContentType "application/x-www-form-urlencoded" -Body @{
        grant_type = "refresh_token"; client_id = "nextrdp-desktop"; refresh_token = $refreshedToken.refresh_token
    }
} catch { $refreshRejected = ([int]$_.Exception.Response.StatusCode -eq 400) }
Assert-Phase2 $refreshRejected "global logout did not revoke refresh-token continuity"

[pscustomobject]@{
    Status = "passed"
    OrganizationId = $organizationId
    WorkspaceId = $workspaceId
    ConnectionId = $connectionId
    PublishedConfigurationId = [Guid]$published.id
    SyncItems = $sync.items.Count
    AuditEvents = $audit.Count
    ServiceAccountTokenValidated = $true
    SecretFreeExportValidated = $true
    EncryptedBackupValidated = $true
    ProductivityValidated = $true
    GroupRoleMappingValidated = $true
    ServiceAccountRotationValidated = $true
    InteractiveOidcPkceValidated = $true
    GlobalLogoutValidated = $true
    CrossWorkspaceIsolationValidated = $true
    CrossOrganizationIsolationValidated = $true
    ScopeDenialValidated = $true
    MalformedInputValidated = $true
} | ConvertTo-Json -Depth 4
