function Test-NextRdpPhaseOneClientPlatform {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$ProductType,
        [Parameter(Mandatory)][int]$BuildNumber,
        [Parameter(Mandatory)][bool]$Is64BitOperatingSystem
    )

    return $Is64BitOperatingSystem -and $ProductType -eq 1 -and $BuildNumber -ge 22000
}
