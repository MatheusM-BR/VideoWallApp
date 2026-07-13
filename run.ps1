# Compila e executa o VideoWallRW.App.
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
dotnet run --project src/VideoWallRW.App @args
