# Executed by the elevated installer, before and after copying program files.
param([switch]$Prepare)
$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath('C:\RWVideoWall')
if ($installRoot -cne 'C:\RWVideoWall') { throw 'Unexpected installation directory.' }
if (Test-Path -LiteralPath $installRoot) {
    $item = Get-Item -LiteralPath $installRoot -Force
    if (!$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'C:\RWVideoWall must be a real directory, not a link.'
    }
    if ($Prepare -and (Get-ChildItem -LiteralPath $installRoot -Force | Select-Object -First 1) -and
        !(Test-Path -LiteralPath "$installRoot\VideoWallRW.App.exe")) {
        throw 'C:\RWVideoWall contains unrelated files. Installation cancelled.'
    }
} else {
    New-Item -ItemType Directory -Path $installRoot | Out-Null
}
# No inherited drive-root permissions: SYSTEM/admins full, all local users read/execute.
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetSecurityDescriptorSddlForm('O:BAG:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)')
Set-Acl -LiteralPath $installRoot -AclObject $acl
if (!$Prepare) {
    # Refuse junctions before resetting child ACLs; never operate outside this root.
    $pending = New-Object 'Collections.Generic.Queue[string]'
    $pending.Enqueue($installRoot)
    while ($pending.Count -gt 0) {
        foreach ($child in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($child.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked file in installation.' }
            if (!$child.FullName.StartsWith($installRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Path outside installation.'
            }
            if ($child.PSIsContainer) { $pending.Enqueue($child.FullName) }
        }
    }
    & "$env:SystemRoot\System32\icacls.exe" "$installRoot\*" /reset /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not protect installed files.' }
}
