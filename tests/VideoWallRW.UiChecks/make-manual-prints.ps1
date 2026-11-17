# Gera os prints do Manual (botão "📖 Manual" do programa) a partir das telas reais,
# com dados fictícios — nenhuma saída, projeto ou rede é tocado.
# Uso (na raiz do repositório, no Windows):  .\tests\VideoWallRW.UiChecks\make-manual-prints.ps1
# Depois: compile/publique o app de novo — os PNGs vão para a pasta Manual ao lado do exe.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
Set-Location $root
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") {
    $env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"; $env:PATH = "$env:DOTNET_ROOT;$env:PATH"
}

$project = 'tests/VideoWallRW.UiChecks/VideoWallRW.UiChecks.csproj'
dotnet run --project $project
if ($LASTEXITCODE -ne 0) { throw 'Verificações da janela principal falharam (veja acima).' }
dotnet run --project $project --no-build -- --auxiliary
if ($LASTEXITCODE -ne 0) { throw 'Verificações das janelas auxiliares falharam (veja acima).' }

$review = 'artifacts/ui-review'
$target = 'src/VideoWallRW.App/Assets/Manual'
New-Item -ItemType Directory -Force -Path $target | Out-Null

# nome no manual  <=  print gerado
$map = [ordered]@{
    'home.png'             = 'layout-1440.png'
    'home-blackout.png'    = 'completion-blackout.png'
    'sources.png'          = 'layout-sources.png'
    'outputs.png'          = 'completion-outputs.png'
    'agenda.png'           = 'layout-agenda.png'
    'agenda-documents.png' = 'layout-agenda-documents.png'
    'pdf.png'              = 'layout-pdf.png'
    'editor-layers.png'    = 'layout-editor-layers.png'
    'editor-brush.png'     = 'layout-editor-brush.png'
    'editor-shape.png'     = 'layout-editor-shape.png'
    'editor-text.png'      = 'layout-editor-text.png'
    'editor-canvas.png'    = 'layout-editor-canvas.png'
    'editor-crop.png'      = 'layout-editor-crop.png'
    'editor-select.png'    = 'layout-editor-select-stretch.png'
    'editor-select-rotate.png' = 'layout-editor-select-rotate.png'
    'remote.png'           = 'completion-remote.png'
    'installation.png'     = 'completion-installation-0.png'
    'hap.png'              = 'completion-hap.png'
    'keymap.png'           = 'completion-keymap.png'
}
$missing = @()
foreach ($entry in $map.GetEnumerator()) {
    $source = Join-Path $review $entry.Value
    if (Test-Path $source) { Copy-Item $source (Join-Path $target $entry.Key) -Force }
    else { $missing += $entry.Value }
}
Write-Host "Prints do manual atualizados em $target ($($map.Count - $missing.Count) de $($map.Count))."
if ($missing.Count -gt 0) { Write-Warning "Não gerados: $($missing -join ', ')" }
