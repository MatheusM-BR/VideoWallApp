# Gera o build PORTATIL do VideoWallRW (self-contained):
# a maquina de destino NAO precisa ter .NET instalado — basta copiar a pasta e rodar o .exe.
#
# Uso:  .\publish.ps1            (x64 — Intel/AMD, o caso normal)
#       .\publish.ps1 win-arm64  (Windows em ARM, se um dia precisar)
param([string]$Runtime = "win-x64")

$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"

$out = "artifacts\publish\VideoWallRW-$Runtime"
dotnet publish src/VideoWallRW.App -c Release -r $Runtime --self-contained true -o $out
if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "Build portatil gerado em: $out"
    Write-Host "Copie a pasta inteira para a maquina de destino e execute VideoWallRW.App.exe"
}
