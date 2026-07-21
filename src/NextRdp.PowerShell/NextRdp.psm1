Set-StrictMode -Version Latest
$script:NextRdpSession = $null

function Connect-NextRdpControlPlane {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidatePattern('^https://|^http://(127\.0\.0\.1|localhost)(:|/)')][string]$Endpoint,
        [Parameter(Mandatory)][string]$ClientId,
        [Parameter(Mandatory)][Security.SecureString]$ClientSecret,
        [string]$Scope = 'inventory.read'
    )
    $address = $Endpoint.TrimEnd('/')
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ClientSecret)
    $plainSecret = $null
    try {
        $plainSecret = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
        $token = Invoke-RestMethod -Method Post -Uri "$address/connect/token" -ContentType 'application/x-www-form-urlencoded' -Body @{
            grant_type = 'client_credentials'; client_id = $ClientId; client_secret = $plainSecret; scope = $Scope
        } -ErrorAction Stop
    }
    finally {
        if ($null -ne $plainSecret) { $plainSecret = $null }
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
    if ([string]::IsNullOrWhiteSpace($token.access_token)) { throw 'The token response did not contain an access token.' }
    $grantedScope = if ($null -ne $token.PSObject.Properties['scope'] -and -not [string]::IsNullOrWhiteSpace([string]$token.scope)) { [string]$token.scope } else { $Scope }
    $script:NextRdpSession = [pscustomobject]@{ Endpoint = $address; AccessToken = [string]$token.access_token; ExpiresAt = [DateTimeOffset]::UtcNow.AddSeconds([int]$token.expires_in) }
    [pscustomobject]@{ Connected = $true; Endpoint = $address; ExpiresAt = $script:NextRdpSession.ExpiresAt; Scope = $grantedScope }
}

function Disconnect-NextRdpControlPlane {
    [CmdletBinding()]
    param()
    $script:NextRdpSession = $null
}

function Invoke-NextRdpRequest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [ValidateSet('Get','Post','Put','Delete')][string]$Method = 'Get', [object]$Body, [string]$IdempotencyKey)
    if ($null -eq $script:NextRdpSession) { throw 'Run Connect-NextRdpControlPlane first.' }
    if ($script:NextRdpSession.ExpiresAt -le [DateTimeOffset]::UtcNow) { throw 'The NextRDP automation token expired. Connect again.' }
    $headers = @{ Authorization = "Bearer $($script:NextRdpSession.AccessToken)"; Accept = 'application/json' }
    if (-not [string]::IsNullOrWhiteSpace($IdempotencyKey)) { $headers['Idempotency-Key'] = $IdempotencyKey }
    $parameters = @{ Method = $Method; Uri = "$($script:NextRdpSession.Endpoint)/$($Path.TrimStart('/'))"; Headers = $headers; ErrorAction = 'Stop' }
    if ($null -ne $Body) { $parameters['ContentType'] = 'application/json'; $parameters['Body'] = ($Body | ConvertTo-Json -Depth 30 -Compress) }
    Invoke-RestMethod @parameters
}

function Get-NextRdpOrganization { [CmdletBinding()] param() Invoke-NextRdpRequest -Path 'api/v1/organizations' }

function Get-NextRdpWorkspace {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId)
    Invoke-NextRdpRequest -Path "api/v1/organizations/$OrganizationId/workspaces"
}

function Get-NextRdpConnection {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId, [Parameter(Mandatory)][Guid]$WorkspaceId, [string]$Search)
    $path = "api/v1/organizations/$OrganizationId/workspaces/$WorkspaceId/connections?pageSize=200"
    if (-not [string]::IsNullOrWhiteSpace($Search)) { $path += '&search=' + [Uri]::EscapeDataString($Search) }
    Invoke-NextRdpRequest -Path $path
}

function Sync-NextRdpWorkspace {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId, [Parameter(Mandatory)][Guid]$WorkspaceId, [string]$ClientVersion = '0.2.0')
    Invoke-NextRdpRequest -Method Post -Path "api/v1/organizations/$OrganizationId/workspaces/$WorkspaceId/sync" -Body @{ cursor = $null; clientVersion = $ClientVersion; knownRevisions = @{} }
}

function Export-NextRdpInventory {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId, [Parameter(Mandatory)][Guid]$WorkspaceId, [Parameter(Mandatory)][string]$Path)
    $result = Invoke-NextRdpRequest -Path "api/v1/organizations/$OrganizationId/workspaces/$WorkspaceId/export"
    $json = $result | ConvertTo-Json -Depth 30
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path), $json, [Text.UTF8Encoding]::new($false))
    Get-Item -LiteralPath $Path
}

function Invoke-NextRdpBulkProvision {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId, [Parameter(Mandatory)][Guid]$WorkspaceId, [Parameter(Mandatory)][string]$InputFile, [string]$IdempotencyKey = ([Guid]::NewGuid().ToString('N')))
    $body = Get-Content -LiteralPath $InputFile -Raw | ConvertFrom-Json
    Invoke-NextRdpRequest -Method Post -Path "api/v1/organizations/$OrganizationId/workspaces/$WorkspaceId/bulk/connections" -Body $body -IdempotencyKey $IdempotencyKey
}

function Test-NextRdpGateway {
    [CmdletBinding()] param([Parameter(Mandatory)][Guid]$OrganizationId, [Parameter(Mandatory)][Guid]$WorkspaceId, [Parameter(Mandatory)][string]$HostName, [int]$Port = 443)
    Invoke-NextRdpRequest -Method Post -Path "api/v1/organizations/$OrganizationId/workspaces/$WorkspaceId/gateway/diagnostics" -Body @{ host = $HostName; port = $Port; expectedServerName = $HostName }
}

Export-ModuleMember -Function Connect-NextRdpControlPlane,Disconnect-NextRdpControlPlane,Get-NextRdpOrganization,Get-NextRdpWorkspace,Get-NextRdpConnection,Sync-NextRdpWorkspace,Export-NextRdpInventory,Invoke-NextRdpBulkProvision,Test-NextRdpGateway
