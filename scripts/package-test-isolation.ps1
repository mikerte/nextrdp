function Get-NextRdpDirectoryFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return 'absent' }
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $records = @('directory:.')
    foreach ($directory in Get-ChildItem -LiteralPath $resolvedPath -Recurse -Force -Directory | Sort-Object FullName) {
        $relativePath = $directory.FullName.Substring($resolvedPath.Length).TrimStart('\', '/').Replace('\', '/')
        $records += "directory:$relativePath"
    }
    foreach ($file in Get-ChildItem -LiteralPath $resolvedPath -Recurse -Force -File | Sort-Object FullName) {
        $relativePath = $file.FullName.Substring($resolvedPath.Length).TrimStart('\', '/').Replace('\', '/')
        $hash = Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256
        $records += "$relativePath|$($file.Length)|$($hash.Hash)"
    }
    $payload = [Text.Encoding]::UTF8.GetBytes(($records -join "`n"))
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($algorithm.ComputeHash($payload))).Replace('-', '') } finally { $algorithm.Dispose() }
}
