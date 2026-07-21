[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Upgrade')][string]$Mode,
    [string]$PreviousPackagePath = '',
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$acceptanceDirectory = Join-Path $root 'artifacts\acceptance'
New-Item -ItemType Directory -Path $acceptanceDirectory -Force | Out-Null
$logPath = Join-Path $acceptanceDirectory "msix-$($Mode.ToLowerInvariant())-elevated.log"

if (-not $Elevated) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', "`"$PSCommandPath`"",
        '-Mode', $Mode,
        '-Elevated'
    )
    if (-not [string]::IsNullOrWhiteSpace($PreviousPackagePath)) {
        $arguments += @('-PreviousPackagePath', "`"$([IO.Path]::GetFullPath($PreviousPackagePath))`"")
    }
    $process = Start-Process -FilePath powershell.exe -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if (Test-Path -LiteralPath $logPath) { Get-Content -LiteralPath $logPath }
    if ($process.ExitCode -ne 0) { throw "Elevated $Mode acceptance failed with exit code $($process.ExitCode). Review $logPath." }
    return
}

$exitCode = 0
Start-Transcript -Path $logPath -Force | Out-Null
try {
    if ($Mode -eq 'Install') {
        & (Join-Path $PSScriptRoot 'test-msix-install.ps1')
    }
    else {
        if ([string]::IsNullOrWhiteSpace($PreviousPackagePath)) { throw 'Upgrade acceptance requires -PreviousPackagePath.' }
        & (Join-Path $PSScriptRoot 'test-msix-upgrade.ps1') -PreviousPackagePath $PreviousPackagePath
    }
}
catch {
    $exitCode = 1
    Write-Error ($_ | Format-List * -Force | Out-String)
}
finally {
    Stop-Transcript | Out-Null
}
exit $exitCode
