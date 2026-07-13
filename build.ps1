# Build do VideoWallRW usando o .NET SDK instalado em modo usuário.
# (O SDK foi instalado em %LOCALAPPDATA%\Microsoft\dotnet para não exigir elevação.
#  Se instalar o SDK máquina-wide depois — winget install Microsoft.DotNet.SDK.8 —
#  este script continua funcionando, mas um `dotnet build` normal também passa a funcionar.)
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet build VideoWallRW.sln -warnaserror @args
