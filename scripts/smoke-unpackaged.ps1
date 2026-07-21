[CmdletBinding()]
param([int]$ObservationSeconds = 5)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'artifacts\publish\win-x64\NextRdp.Desktop.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Published executable not found. Run scripts\publish-windows.ps1 first.' }
$smokeDataRoot = Join-Path $root 'artifacts\smoke-data'
$smokeDataPath = Join-Path $smokeDataRoot "run-$([Guid]::NewGuid().ToString('N'))"
$resolvedRoot = [IO.Path]::GetFullPath($root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$resolvedSmokeDataPath = [IO.Path]::GetFullPath($smokeDataPath)
if (-not $resolvedSmokeDataPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Smoke data must remain inside the project directory.' }
New-Item -ItemType Directory -Path $resolvedSmokeDataPath -Force | Out-Null
$environmentVariable = 'NEXTRDP_TEST_DATA_DIRECTORY'
$previousTestDataPath = [Environment]::GetEnvironmentVariable($environmentVariable, [EnvironmentVariableTarget]::Process)
$process = $null
try {
    [Environment]::SetEnvironmentVariable($environmentVariable, $resolvedSmokeDataPath, [EnvironmentVariableTarget]::Process)
    $process = Start-Process -FilePath $exe -PassThru
    $databasePath = Join-Path $resolvedSmokeDataPath 'nextrdp.db'
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw "NextRDP exited during startup with code $($process.ExitCode)." }
        if ((Test-Path -LiteralPath $databasePath) -and $process.MainWindowHandle -ne 0 -and $process.Responding) { break }
    }
    $process.Refresh()
    if (-not (Test-Path -LiteralPath $databasePath)) { throw 'NextRDP did not create its isolated smoke-test inventory within 15 seconds.' }
    if ($process.MainWindowHandle -eq 0 -or -not $process.Responding) { throw 'NextRDP did not expose a responsive main window within 15 seconds.' }
    Start-Sleep -Seconds $ObservationSeconds
    $process.Refresh()
    if ($process.HasExited) { throw "NextRDP exited during the smoke interval with code $($process.ExitCode)." }
    if (-not $process.Responding) { throw 'NextRDP stopped responding during the smoke interval.' }
    Write-Host "Isolated startup smoke passed; NextRDP remained responsive for $ObservationSeconds seconds and created a clean local inventory under $resolvedSmokeDataPath."
}
finally {
    [Environment]::SetEnvironmentVariable($environmentVariable, $previousTestDataPath, [EnvironmentVariableTarget]::Process)
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(3000)) { Stop-Process -Id $process.Id }
    }
}
