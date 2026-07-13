# Fase 0 — Fundação: D3D11 multi-monitor

## Objetivo
Solução .NET com a estrutura definitiva de projetos e um frame loop D3D11
apresentando um **quad animado** em janelas fullscreen borderless em 1+ monitores,
a 60fps estáveis. Nenhum vídeo ainda. Esta fase valida o risco nº 1 do projeto:
apresentação simultânea em múltiplos monitores (possivelmente com taxas de
atualização diferentes).

## Pré-requisitos
- Windows 11, .NET 8 SDK, GPU com D3D11 (feature level 11_0).
- Ler `ARQUITETURA.md` §3 (engine) e §10 (riscos).

## Entregáveis
1. `VideoWallRW.sln` com os projetos e referências de `docs/DESENVOLVIMENTO.md`.
2. `GraphicsDevice`, `RenderThread`, `OutputWindow`, `SwapChainOutput`.
3. App WPF mínimo: lista de monitores com checkbox "ativar saída" + FPS por saída.
4. Logging Serilog funcionando.

---

## Tarefas

### T0.1 — Solução, projetos e higiene
- `git init` no diretório do projeto (ainda não é repo).
- Criar solução e projetos:
  ```
  dotnet new sln -n VideoWallRW
  dotnet new classlib -n VideoWallRW.Core   -o src/VideoWallRW.Core
  dotnet new classlib -n VideoWallRW.Engine -o src/VideoWallRW.Engine
  dotnet new classlib -n VideoWallRW.Media  -o src/VideoWallRW.Media
  dotnet new classlib -n VideoWallRW.Sources -o src/VideoWallRW.Sources
  dotnet new wpf      -n VideoWallRW.App    -o src/VideoWallRW.App
  dotnet new xunit    -n VideoWallRW.Core.Tests -o tests/VideoWallRW.Core.Tests
  ```
- `Directory.Build.props` na raiz: `net8.0` (`net8.0-windows` para App),
  `Nullable=enable`, `LangVersion=12`, `TreatWarningsAsErrors=true`.
- `.gitignore` (template VisualStudio), `.editorconfig` básico.
- Referências entre projetos conforme DESENVOLVIMENTO.md.
- NuGet: `Vortice.Direct3D11`, `Vortice.DXGI`, `Vortice.D3DCompiler`,
  `Vortice.Mathematics` (Engine); `Serilog`, `Serilog.Sinks.File`,
  `Serilog.Sinks.Debug` (App configura, demais usam `ILogger`);
  `CommunityToolkit.Mvvm` (App).
- Commit inicial.

### T0.2 — GraphicsDevice
`src/VideoWallRW.Engine/Graphics/GraphicsDevice.cs`:

```csharp
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }   // immediate; só a render thread usa
    public IDXGIFactory2 Factory { get; }
    public bool TearingSupported { get; }
    public static GraphicsDevice Create();
}
```

- `D3D11.D3D11CreateDevice` com `DeviceCreationFlags.BgraSupport`
  (+ `Debug` em DEBUG, com fallback silencioso se o SDK layer não estiver instalado).
- Feature levels: tentar 11_1, aceitar 11_0.
- `TearingSupported`: `IDXGIFactory5.PresentAllowTearing`.
- Ativar proteção multithread do device (`ID3D11Multithread.SetMultithreadProtected(true)`)
  — a fase 6 (D3D11VA) exige; ligar desde já.

### T0.3 — RenderThread e fila de comandos
`src/VideoWallRW.Engine/RenderThread.cs`:

```csharp
public sealed class RenderThread : IDisposable
{
    public void Start(); public void Stop();
    public void Post(EngineCommand cmd);          // thread-safe (Channel)
    public event Action<EngineStats>? StatsUpdated; // ~2x/segundo, fps por saída
}
```

- Thread dedicada (`IsBackground = true`, nome "RenderThread").
- Loop: drenar fila de comandos → atualizar cena (tempo via `Stopwatch`) →
  renderizar cada saída → present de cada saída → contabilizar fps/frametime.
- `EngineCommand` por enquanto: `EnableOutput(monitorId)`, `DisableOutput(monitorId)`,
  `Shutdown`. Modelar como records em `Core/Commands/`.
- Sem saída ativa: dormir 10ms por iteração (não fritar CPU).

### T0.4 — Janela de saída Win32 pura
`src/VideoWallRW.Engine/Output/OutputWindow.cs` — **não usar WPF** para janelas de
saída. P/Invoke mínimo: `RegisterClassExW`, `CreateWindowExW`, `DestroyWindow`,
`DefWindowProcW`, `PeekMessageW`/`TranslateMessage`/`DispatchMessageW`, `ShowWindow`,
`SetWindowPos`, `ShowCursor`.

- Estilo `WS_POPUP`, sem borda, posicionada exatamente no retângulo do monitor
  alvo (`DesktopCoordinates` do DXGI output). **Borderless windowed, nunca
  exclusive fullscreen.**
- Criar as janelas **na render thread** e bombear mensagens com `PeekMessage`
  uma vez por frame (evita thread extra de mensagens).
- Cursor oculto sobre a janela de saída; `Esc` com a janela focada não fecha nada
  (saídas só fecham por comando).
- `WM_CLOSE` → ignorar (logar); monitor removido é tratado em T0.7.

### T0.5 — SwapChainOutput
`src/VideoWallRW.Engine/Output/SwapChainOutput.cs`:

- `CreateSwapChainForHwnd`, `DXGI_SWAP_EFFECT_FLIP_DISCARD`, 2 buffers,
  `B8G8R8A8_UNorm`, `AlphaMode.Ignore`; flag `ALLOW_TEARING` se suportado.
- Estratégia de present (mitigação do risco de vsync misto):
  - Saída marcada como **primária** (a primeira ativada): `Present(1, 0)` — vsync.
  - Demais: `Present(0, DXGI_PRESENT_ALLOW_TEARING)` quando suportado, senão `Present(0,0)`.
- `MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER)` para bloquear Alt+Enter.
- Tratar `DXGI_ERROR_DEVICE_REMOVED`/`RESET` no Present: logar e propagar para o
  handler de device-lost do RenderThread (nesta fase: derrubar saídas e reportar).

### T0.6 — Quad animado (pipeline mínimo)
- `Shaders/Fullscreen.hlsl`: VS gera fullscreen triangle (3 vértices, sem vertex
  buffer, via `SV_VertexID`); PS recebe `cbuffer { float time; float2 resolution; }`
  e desenha um gradiente animado + uma barra que se move 1px/frame (barra é o
  detector visual de frame drop: qualquer soluço aparece como salto).
- Compilar HLSL em runtime com `Vortice.D3DCompiler` (`Compiler.Compile`), cachear blobs.
- Desenhar direto no backbuffer de cada saída (composição de verdade vem na fase 1).

### T0.7 — Enumeração e hotplug de monitores
`src/VideoWallRW.Engine/Output/MonitorInfo.cs` + `MonitorEnumerator`:

- Enumerar via DXGI (`Adapter.EnumOutputs`): nome do device, `DesktopCoordinates`,
  formato/rotação. Id estável = `DeviceName` (ex: `\\.\DISPLAY1`).
- Reenumerar sob demanda (botão "atualizar" na UI). Hotplug automático fica pro
  backlog; monitor sumir com saída ativa → desativar a saída, logar, avisar UI.

### T0.8 — App WPF mínimo
- Janela única: lista de monitores (nome, resolução, posição) com checkbox
  "Saída ativa"; label de FPS por saída (do evento `StatsUpdated`); botão
  "Atualizar monitores".
- App inicializa Serilog, cria `GraphicsDevice` + `RenderThread` no startup,
  faz dispose ordenado no exit (parar thread → dispose saídas → dispose device
  → `ReportLiveDeviceObjects` em DEBUG).
- Manifesto com DPI awareness **PerMonitorV2** (senão as coordenadas de monitor
  mentem com scaling ≠ 100%).

---

## Armadilhas conhecidas
- **DPI**: sem PerMonitorV2, `DesktopCoordinates` do DXGI (pixels físicos) não
  batem com o que o WPF enxerga. As janelas de saída usam sempre pixels físicos.
- Debug layer só existe com "Graphics Tools" instalado — fallback sem a flag.
- `Present(0, ALLOW_TEARING)` exige a flag também na **criação** da swapchain.
- Janela Win32 criada numa thread precisa do pump **na mesma thread** (T0.4 já prevê).
- Não usar `Thread.Sleep` para regular fps — o `Present(1,0)` da primária já
  regula o loop.

## DoD — verificação manual
1. `dotnet build -warnaserror` limpo; testes do Core passam (ainda que triviais).
2. Ativar saída em 2 monitores: gradiente animado + barra móvel em ambos.
3. Barra move suave por 5 minutos (sem saltos visíveis) com FPS ≈ refresh da primária.
4. Ativar/desativar saídas repetidamente (10×) sem crash e sem vazamento
   (`ReportLiveDeviceObjects` limpo ao sair).
5. Mover a janela do App, redimensionar, minimizar: saídas não tremem.
6. Deixar 30 min rodando: memória estável no Gerenciador de Tarefas.
