# Run with PowerShell 7 from the repository root. Never packages local user data.
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.0.0',
    [string]$Compiler = "$PSScriptRoot\artifacts\tools\InnoSetup\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7.' }
if (!(Test-Path -LiteralPath $Compiler)) { throw 'Install Inno Setup and pass -Compiler with the ISCC.exe path.' }

$releaseId = "$Version-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$releaseRoot = Join-Path $PSScriptRoot "artifacts/releases/$releaseId"
$payload = Join-Path $releaseRoot 'application'
$setupDir = Join-Path $releaseRoot 'installer'
New-Item -ItemType Directory -Path $payload,$setupDir | Out-Null
$publicKeyFile = Join-Path $releaseRoot 'package-public-key.pem'
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    # Only the public key is persisted. The release signing key never leaves memory.
    [IO.File]::WriteAllText($publicKeyFile, $rsa.ExportSubjectPublicKeyInfoPem())
    & dotnet publish "$PSScriptRoot/src/VideoWallRW.App/VideoWallRW.App.csproj" `
        -c Release -r win-x64 --self-contained true -o $payload `
        "-p:Version=$Version" "-p:InformationalVersion=$releaseId" `
        '-p:DebugType=None' '-p:DebugSymbols=false' '-p:PublishDocumentationFiles=false' `
        '-p:ProtectedPackage=true' "-p:PackagePublicKeyPath=$publicKeyFile" --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Release publication failed.' }

    foreach ($required in @('VideoWallRW.App.exe','coreclr.dll','PresentationFramework.dll',
        'avcodec-61.dll','avformat-61.dll','avutil-59.dll','swresample-5.dll','swscale-8.dll',
        'Processing.NDI.Lib.x64.dll','Processing.NDI.Lib.Licenses.txt',
        'Rendering/Shaders/Composite.hlsl','Rendering/Shaders/SliceOutput.hlsl')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $required))) { throw "Missing runtime component: $required" }
    }
    if (Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object Extension -In '.pdb','.cs','.csproj','.ps1') {
        throw 'Source/debug files found in distribution.'
    }
    Copy-Item -LiteralPath "$PSScriptRoot/THIRD-PARTY.md" -Destination $payload
    Copy-Item -LiteralPath "$PSScriptRoot/installer/LEIA-ME.txt" -Destination $payload

    $inventory = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
        $inventory[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
    $inventoryPath = Join-Path $payload 'package-integrity.json'
    [IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    $signature = $rsa.SignData([IO.File]::ReadAllBytes($inventoryPath),
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pss)
    [IO.File]::WriteAllBytes((Join-Path $payload 'package-integrity.sig'), $signature)

    $check = Start-Process -FilePath (Join-Path $payload 'VideoWallRW.App.exe') `
        -ArgumentList '--verify-package' -WindowStyle Hidden -PassThru -Wait
    if ($check.ExitCode -ne 0) { throw "Published package integrity failed: $($check.ExitCode)" }

    & $Compiler /Qp "/DPayloadDir=$payload" "/DInstallerOutput=$setupDir" "/DReleaseVersion=$Version" `
        "$PSScriptRoot/installer/VideoWallRW.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $setupDir "RW-VideoWall-$Version-Setup-x64.exe"
    $checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$installer.sha256", "$checksum  $([IO.Path]::GetFileName($installer))`n")
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'artifacts/releases/latest.json'),
        (@{ version=$Version; release=$releaseId; installer=$installer; application=$payload; sha256=$checksum } | ConvertTo-Json))
    Write-Host "Installer ready: $installer"
    Write-Host "SHA-256: $checksum"
} finally {
    $rsa.Dispose()
}
