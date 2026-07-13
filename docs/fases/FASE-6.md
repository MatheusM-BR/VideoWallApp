# Fase 6 — Performance: decode por hardware, HAP e preroll

## Objetivo
Escalar de "1 vídeo roda" para "um show roda": decodificação por hardware
(D3D11VA) com fallback transparente, suporte ao codec **HAP** (o equivalente
open-source do DXV do Resolume — muitos layers 4K com CPU quase zero),
telemetria de drops e afinação do disparo (preroll). Ao final, medir e documentar
a capacidade real da máquina de referência.

## Pré-requisitos
- Fase 5 concluída (DoD verificado).
- NuGet no Media: `Snappier` (decompressão HAP), `System.IO.Hashing` (xxHash,
  se ainda não entrou com thumbnails/relink).

## Entregáveis
1. Decode D3D11VA zero-copy (NV12 na GPU → shader NV12→RGBA).
2. Pool de decoders HW com limite e fallback SW transparente.
3. `HapVideoSource` (demux FFmpeg + Snappy + upload BC1/BC3, HapQ com shader).
4. Conversor em lote para HAP embutido (ffmpeg.exe).
5. HUD de performance + contadores de drop por estágio.
6. Relatório de capacidade (`docs/PERF.md`).

---

## Tarefas

### T6.1 — Contexto de hardware compartilhado (FFmpeg ⇄ engine)
No `MediaDecoder`:
- Criar `AVHWDeviceContext` do tipo `AV_HWDEVICE_TYPE_D3D11VA` **embrulhando o
  nosso device** (alocar com `av_hwdevice_ctx_alloc`, preencher
  `AVD3D11VADeviceContext.device` com o `ID3D11Device` do engine + `AddRef`,
  `av_hwdevice_ctx_init`) — assim a textura decodificada já vive no device certo,
  zero cópia entre devices.
- `codecCtx->hw_device_ctx = av_buffer_ref(...)`; callback `get_format`
  escolhe `AV_PIX_FMT_D3D11` quando oferecido, senão cai no caminho SW.
- A proteção multithread do device (ligada na fase 0) é obrigatória aqui.

### T6.2 — Caminho de frame zero-copy
- Frame HW: `frame->data[0] = ID3D11Texture2D` (NV12, texture array),
  `frame->data[1] = índice do array`.
- Na render thread: `CopySubresourceRegion` do slice do array para uma textura
  NV12 própria do clip (double buffer) — a textura do decoder é reciclada pelo
  FFmpeg, não pode ser referenciada além do frame.
- SRVs sobre a textura NV12: plano Y como `R8_UNorm`, plano UV como `R8G8_UNorm`
  (criar a textura com formato `NV12` e SRVs com os formatos por plano).
- `Nv12ToRgba.hlsl`: conversão BT.709 (BT.601 para SD) direto no passe de
  composição (variante do pixel shader; evita passe extra).
- O `DecodedFrame` da fase 1 vira união: buffer CPU **ou** referência de textura
  — `VideoFileSource` trata os dois.

### T6.3 — Pool e fallback
- `HwDecoderPool`: máximo N decoders HW simultâneos (config, default 8; NVIDIA
  GeForce historicamente limita sessões NVDEC). Pedido acima do limite → decoder
  SW automaticamente (logar downgrade).
- Falha ao abrir HW (codec sem suporte, driver) → SW sem erro visível ao usuário
  (badge "SW" discreta na célula, para diagnóstico).
- H.264, H.265 e VP9 via HW quando disponível; o resto SW.

### T6.4 — HAP nativo
`src/VideoWallRW.Media/Hap/HapVideoSource.cs`:
- Demux com FFmpeg (MOV); identificar por `codec_tag`: `Hap1` (→BC1), `Hap5`
  (→BC3), `HapY` (HapQ →BC3 + shader YCoCg), `HapA`/`HapM` fora do escopo v1.
- **Não usar o decoder HAP do FFmpeg** (ele descomprime DXT→RGBA na CPU e joga
  fora a vantagem). Parse do payload conforme spec Vidvox (header de seção:
  tamanho 3 bytes + tipo 1 byte; tipo indica compressor: none/snappy; seções
  `decode instructions` com chunks para multi-thread).
- Snappy via `Snappier` → bytes DXT prontos → upload em textura
  `BC1_UNorm`/`BC3_UNorm` (dynamic/staging; RowPitch de BC = `(w/4)×blockBytes`).
- HapQ: amostrar BC3 e converter YCoCg→RGB no pixel shader (flag no cbuffer).
- Playback: mesma `FrameQueue`/transporte da fase 1 — só muda o "decoder".
  Seek é trivial (todo frame é keyframe).

### T6.5 — Conversor em lote
- `ffmpeg.exe` (build **full/GPL ok** — processo externo, sem link) em
  `third_party/ffmpeg-cli/`.
- UI "Ferramentas → Converter para HAP": fila de arquivos, destino, preset
  (`-c:v hap` / `-format hap_q`), progresso por arquivo (parse do stderr),
  botão "substituir clips na grade pelos convertidos".

### T6.6 — Preroll e latência de disparo
- Medir (log) o tempo Trigger→primeiro frame no ar; alvo < 100ms arquivo local.
- Otimizações: manter `MediaDecoder` do clip **anterior** da mesma célula em
  cache morno por 5s (retrigger instantâneo); abrir arquivo com
  `fflags=fastseek`; primeira imagem decodificada já no `Arming` da fase 2 —
  garantir que o caminho HW também faz preroll.
- Opcional (config): "armar coluna" — pré-abrir decoders da próxima coluna
  (usado pelo sequenciador da fase 8).

### T6.7 — Telemetria e HUD
- Contadores por estágio: frames decodificados, drops de fila (decoder produziu,
  render descartou por atraso), drops de render (frametime > 1.5× alvo),
  uploads/s, decoders HW/SW ativos, VRAM aproximada (DXGI
  `QueryVideoMemoryInfo`).
- HUD na UI (painel de diagnóstico) + overlay opcional nas saídas (F10): fps,
  frametime p95, drops última hora.
- Aviso visual (borda vermelha no monitor de programa) quando houver drop no ar.

### T6.8 — Medição de capacidade
- Roteiro reprodutível em `docs/PERF.md`: máquina de referência, 1/2/4/8 vídeos
  1080p60 H.264 HW, mesmos em HAP, 2×4K, +NDI. Registrar fps/drops/CPU/GPU.
  Este documento vira o "manual de dimensionamento" para instalações.

---

## Armadilhas conhecidas
- Texturas do pool do FFmpeg **não** podem ser guardadas além do
  `av_frame_unref` — copiar sempre (T6.2).
- `CopySubresourceRegion` de NV12: copiar a subresource inteira (os dois planos
  vêm juntos); não tentar copiar planos separadamente.
- Driver Intel/AMD/NVIDIA divergem no `get_format` — testar nos três se possível;
  fallback SW cobre o resto.
- BC RowPitch: alinhamento por blocos 4×4; largura não múltipla de 4 existe em
  HAP? (spec exige múltiplo de 4 — validar no import e rejeitar com mensagem).
- HUD não pode custar performance: coletar em contadores atômicos, agregar 2×/s.

## DoD — verificação manual
1. Vídeo H.264 4K60 reproduz suave com CPU de decode ~0 (Task Manager: GPU
   Video Decode ativo).
2. Máquina de referência: 4× 1080p60 H.264 simultâneos + 1 NDI sem drops no HUD.
3. 6× HAP 1080p60 simultâneos com CPU total < 30%.
4. Puxar além do limite de decoders HW → clips extras caem para SW sem crash,
   badge correta.
5. Trigger→ar < 100ms (log) para arquivo local armado.
6. Conversor: converter 3 arquivos e substituir na grade, tocam idênticos.
7. Soak 2h com grade mista (HW+HAP+NDI): zero crescimento de memória/VRAM.
