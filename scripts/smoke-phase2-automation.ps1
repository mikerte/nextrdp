param([string]$BaseUri = "http://127.0.0.1:5090")

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$originalSecret = $env:NEXTRDP_CLIENT_SECRET
$exportPath = Join-Path ([IO.Path]::GetTempPath()) "nextrdp-cli-export-$([Guid]::NewGuid().ToString('N')).json"

function Assert-Automation([bool]$Condition, [string]$Message) { if (-not $Condition) { throw "Phase 2 automation assertion failed: $Message" } }
function Invoke-Phase2Cli([string[]]$Arguments) {
    $previousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = "Continue"
        $output = @(& dotnet $script:cli @Arguments 2>&1)
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousPreference
    }
    return [pscustomobject]@{ Output = $output; ExitCode = $exitCode }
}
function Invoke-Admin([string]$Method, [string]$Path, [object]$Body) {
    $parameters = @{ Uri = "$BaseUri$Path"; Method = $Method; WebSession = $script:session; Headers = @{ "X-CSRF-TOKEN" = $script:csrf }; UseBasicParsing = $true }
    if ($null -ne $Body) { $parameters.ContentType = "application/json"; $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
    $response = Invoke-WebRequest @parameters
    if ([string]::IsNullOrWhiteSpace([string]$response.Content)) { return $null }
    $response.Content | ConvertFrom-Json
}

$account = $null
try {
    $null = Invoke-WebRequest -Uri "$BaseUri/auth/development-signin?returnUrl=%2F" -SessionVariable session -MaximumRedirection 5 -UseBasicParsing
    $script:csrf = [string](Invoke-RestMethod -Uri "$BaseUri/auth/csrf" -WebSession $script:session).requestToken
    $organizations = Invoke-Admin GET "/api/v1/organizations" $null
    Assert-Automation ($organizations.Count -gt 0) "no organization is available"
    $organizationId = [Guid]$organizations[0].id
    $workspaces = Invoke-Admin GET "/api/v1/organizations/$organizationId/workspaces" $null
    Assert-Automation ($workspaces.Count -gt 0) "no workspace is available"
    $workspaceId = [Guid]$workspaces[0].id
    $account = Invoke-Admin POST "/api/v1/organizations/$organizationId/service-accounts" @{
        name = "Automation smoke $([Guid]::NewGuid().ToString('N').Substring(0,8))"
        workspaceId = $workspaceId
        scopes = @("inventory.read", "configuration.read")
        expiresAtUtc = [DateTimeOffset]::UtcNow.AddMinutes(30).ToString("o")
    }
    $env:NEXTRDP_CLIENT_SECRET = [string]$account.clientSecret
    $script:cli = Join-Path $PSScriptRoot "..\src\NextRdp.Cli\bin\Release\net10.0\nextrdp.dll"
    Assert-Automation (Test-Path -LiteralPath $script:cli) "release CLI assembly is missing"
    $cliResult = Invoke-Phase2Cli @("--endpoint", $BaseUri, "--client-id", [string]$account.clientId, "organizations")
    Assert-Automation ($cliResult.ExitCode -eq 0) "CLI organization discovery failed: $($cliResult.Output -join ' ')"
    Assert-Automation (($cliResult.Output -join "`n") -notmatch [Regex]::Escape([string]$account.clientSecret)) "CLI output echoed the service-account secret"
    $cliResult = Invoke-Phase2Cli @("--endpoint", $BaseUri, "--client-id", [string]$account.clientId, "connections", "--organization", [string]$organizationId, "--workspace", [string]$workspaceId)
    Assert-Automation ($cliResult.ExitCode -eq 0) "CLI connection discovery failed: $($cliResult.Output -join ' ')"
    $cliResult = Invoke-Phase2Cli @("--endpoint", $BaseUri, "--client-id", [string]$account.clientId, "export", "--organization", [string]$organizationId, "--workspace", [string]$workspaceId, "--output", $exportPath)
    Assert-Automation ($cliResult.ExitCode -eq 0 -and (Test-Path -LiteralPath $exportPath)) "CLI secret-free export failed: $($cliResult.Output -join ' ')"
    $exportText = Get-Content -LiteralPath $exportPath -Raw
    Assert-Automation ($exportText -notmatch '(?i)password|clientSecret|credentialReference') "CLI export contains a credential-shaped field"

    Import-Module (Join-Path $PSScriptRoot "..\src\NextRdp.PowerShell\NextRdp.psd1") -Force
    $secureSecret = ConvertTo-SecureString ([string]$account.clientSecret) -AsPlainText -Force
    $connected = Connect-NextRdpControlPlane -Endpoint $BaseUri -ClientId $account.clientId -ClientSecret $secureSecret -Scope "inventory.read"
    Assert-Automation ($connected.Connected -eq $true) "PowerShell service-account authentication failed"
    $powershellConnections = Get-NextRdpConnection -OrganizationId $organizationId -WorkspaceId $workspaceId
    Assert-Automation ($null -ne $powershellConnections) "PowerShell connection discovery returned no response"
    Disconnect-NextRdpControlPlane

    Invoke-Admin DELETE "/api/v1/organizations/$organizationId/service-accounts/$($account.id)" $null
    $account = $null
    Remove-Item Env:NEXTRDP_CLIENT_SECRET -ErrorAction SilentlyContinue
    $failureResult = Invoke-Phase2Cli @("--endpoint", $BaseUri, "--client-id", "missing", "organizations")
    Assert-Automation ($failureResult.ExitCode -eq 3) "CLI authentication failure did not return stable exit code 3"
    Assert-Automation (($failureResult.Output -join "`n") -match 'authentication_required') "CLI authentication failure did not return the stable error code"

    [pscustomobject]@{ Status = "passed"; OrganizationId = $organizationId; WorkspaceId = $workspaceId; Cli = $true; PowerShell = $true; SecretFreeOutput = $true; StableExitCodes = $true } | ConvertTo-Json
}
finally {
    if ($null -ne $account) {
        try { Invoke-Admin DELETE "/api/v1/organizations/$organizationId/service-accounts/$($account.id)" $null | Out-Null } catch { }
    }
    if ($null -eq $originalSecret) { Remove-Item Env:NEXTRDP_CLIENT_SECRET -ErrorAction SilentlyContinue } else { $env:NEXTRDP_CLIENT_SECRET = $originalSecret }
    Remove-Item -LiteralPath $exportPath -Force -ErrorAction SilentlyContinue
}
