# Fase 5 — Fontes ao vivo: NDI e placa de captura

## Objetivo
Clips deixam de ser só arquivos: fontes **NDI** (rede) e **dispositivos de
captura** (Media Foundation) entram na grade como qualquer clip, com descoberta
de fontes, negociação de formato e — crítico — **reconexão automática**. Uma
fonte ao vivo que cai e volta não pode exigir intervenção.

## Pré-requisitos
- Fase 4 concluída (DoD verificado).
- **NDI SDK 6** instalado (download gratuito em ndi.video). O runtime
  redistribuível instala `Processing.NDI.Lib.x64.dll`; o app deve procurar em
  `%NDI_RUNTIME_DIR_V6%` e, se ausente, mostrar diálogo com link do runtime
  (não embutir a DLL — licença pede o instalador oficial).
- NuGet no Sources: `Vortice.MediaFoundation`; fallback `DirectShowLib` só se
  necessário (ver T5.6).

## Entregáveis
1. Camada P/Invoke NDI mínima + `NdiSource : IFrameSource`.
2. Descoberta de fontes NDI (navegador na UI).
3. `CaptureDeviceSource : IFrameSource` via Media Foundation.
4. Enumeração de dispositivos/formatos de captura na UI.
5. Reconexão automática para ambos; novos `SourceDescriptor`s no Core.

---

## Tarefas

### T5.1 — Core: descritores
```csharp
public sealed record NdiSourceDescriptor(string SourceName, NdiBandwidth Bandwidth)
    : SourceDescriptor;   // SourceName = "MAQUINA (Canal)" como o NDI anuncia
public sealed record CaptureSourceDescriptor(string SymbolicLink, string FriendlyName,
    int Width, int Height, double Fps, string SubtypeFourCc) : SourceDescriptor;
```
- `SymbolicLink` é o id estável do MF (sobrevive a reboot; FriendlyName é exibição).

### T5.2 — P/Invoke NDI
`src/VideoWallRW.Sources/Ndi/NdiNative.cs` — só o necessário do
`Processing.NDI.Lib`:
- `NDIlib_initialize/destroy`
- `NDIlib_find_create_v2`, `NDIlib_find_wait_for_sources`,
  `NDIlib_find_get_current_sources`, `NDIlib_find_destroy`
- `NDIlib_recv_create_v3` (pedir `color_format_BGRX_BGRA` — elimina conversão
  YUV no shader; bandwidth: `highest` ou `lowest` conforme descriptor),
  `NDIlib_recv_capture_v2`, `NDIlib_recv_free_video_v2`, `NDIlib_recv_destroy`
- Structs com layout exato do header do SDK (conferir `MarshalAs` de strings
  UTF-8). Carregar a DLL com `NativeLibrary.Load` a partir do caminho resolvido.

### T5.3 — NdiFinderService
- Thread única com um `NDIlib_find` global: mantém lista observável de fontes na
  rede (nome + endereço), atualizada a cada ~2s.
- UI "Fontes" (nova aba do painel): lista as fontes NDI ao vivo; arrastar para
  uma célula da grade cria o clip NDI (ou botão "adicionar à célula selecionada").

### T5.4 — NdiSource : IFrameSource
- Thread de recepção por instância: `recv_capture_v2` com timeout 100ms;
  frame de vídeo → copiar para buffer intermediário (respeitando `line_stride`)
  → sinalizar; render thread faz upload (dynamic texture, igual fase 1) no
  `AcquireFrame`. Resolução pode **mudar no meio** (NDI permite) → recriar
  textura quando `xres/yres` mudarem.
- Frames de áudio: liberar imediatamente (áudio NDI = backlog).
- **Watchdog de reconexão**: sem frame por 5s → destruir receiver → recriar →
  estado `Arming` (UI mostra "reconectando"); a fonte pode nem existir ainda
  (projeto abriu antes da máquina remota ligar) — ficar tentando com backoff
  (2s, 4s, máx 10s) para sempre, sem log-spam (logar só transições).
- Clip NDI ignora transporte (pause congela no último frame recebido — o
  `AcquireFrame` simplesmente para de trocar; **não** pausar a recepção).

### T5.5 — CaptureDeviceSource (Media Foundation)
`src/VideoWallRW.Sources/Capture/`:
- `MFStartup` uma vez (e `MFShutdown` no exit).
- Enumeração: `MFEnumDeviceSources(VIDCAP)` → FriendlyName + SymbolicLink; para
  cada device, listar media types do stream 0 (resolução/fps/subtype) para o
  picker da UI.
- Fonte: `MFCreateDeviceSource` → `MFCreateSourceReaderFromMediaSource` →
  `SetCurrentMediaType` com o formato do descriptor (preferir **NV12**, depois
  YUY2, depois RGB32 — o que o driver der).
- Thread de leitura: loop `ReadSample` síncrono; sample → `ConvertToContiguousBuffer`
  → buffer intermediário → upload na render thread. Conversão NV12/YUY2→RGBA em
  **pixel shader** (escrever `YuvToRgb.hlsl` com BT.601/BT.709 selecionável por
  cbuffer; 601 para SD, 709 para HD como heurística).
- Device perdido (`MF_E_VIDEO_RECORDING_DEVICE_INVALIDATED`, cabo puxado):
  fechar tudo, estado `Arming`, tentar reabrir com backoff — igual NDI.
- Dispositivo já em uso por outro app: erro legível no clip ("dispositivo em uso").

### T5.6 — Fallback DirectShow (só se necessário)
- Testar com as placas-alvo reais (Elgato/AverMedia/Magewell). Se MF funcionar
  com todas, **pular esta tarefa** e registrar no BACKLOG. Caso alguma só
  apareça via DirectShow, implementar `DshowCaptureSource` com `DirectShowLib`
  (SampleGrabber) atrás da mesma interface.

### T5.7 — UI de fontes ao vivo
- Aba "Fontes": duas seções (NDI descobertas / Dispositivos de captura), botão
  refresh, arrastar→célula.
- Célula de clip ao vivo: ícone distintivo (antena/placa), badge de estado
  (verde ok, amarelo reconectando, vermelho erro).
- Propriedades do clip NDI: bandwidth (Highest/Lowest). Do clip de captura:
  combo de formato (trocar = reabrir device).

---

## Armadilhas conhecidas
- Strings NDI são UTF-8; marshal errado corrompe nomes com acento.
- `recv_capture_v2` retorna frame que **deve** ser liberado com o free
  correspondente — vazamento aqui trava o receiver em minutos.
- MF `ReadSample` síncrono trava se o device congelar: usar
  `MF_READWRITE_DISABLE_CONVERTERS=false` e timeout via flags do reader; se
  travar > 3s, abandonar a thread e reabrir (thread órfã morre com o processo —
  logar).
- Duas células apontando para a MESMA fonte NDI/dispositivo: compartilhar a
  instância via cache por descriptor com refcount (placa não abre duas vezes).
- Stride do NDI (`line_stride_in_bytes`) e do MF (`MF_MT_DEFAULT_STRIDE`, pode
  ser negativo = bottom-up!) — respeitar sempre.
- Não chamar nada do NDI/MF na render thread (descoberta e open podem bloquear
  segundos).

## DoD — verificação manual
1. Fonte NDI (usar "NDI Test Patterns" da NDI Tools em outra máquina ou na
   mesma) aparece no navegador, entra na grade e vai ao ar.
2. Derrubar a fonte NDI (fechar o app remoto) → badge amarela; religar → volta
   sozinha em ≤ 10s. Repetir 5×.
3. Placa de captura com HDMI ao vivo na grade; puxar o cabo USB da placa e
   reconectar → volta sozinha.
4. Pausar um clip NDI congela o frame; retomar volta ao vivo.
5. Mesma fonte NDI em 2 layers = 1 conexão de rede (verificar por log/contador).
6. NDI 1080p60 + captura 1080p60 + 1 vídeo em arquivo simultâneos sem drops
   visíveis.
7. Abrir projeto com NDI inexistente na rede: clip fica "reconectando", programa
   100% funcional no resto.
