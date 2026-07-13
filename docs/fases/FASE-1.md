# Fase 1 — Player de vídeo (FFmpeg, decode por software)

## Objetivo
Um arquivo de vídeo decodificado por FFmpeg (caminho por software primeiro —
hardware é fase 6), virando textura D3D11, desenhado num **render target de
composição** e apresentado nas saídas. Transporte completo: play, pause (segura o
frame), seek, loop, velocidade. É aqui que nascem o relógio e a semântica de
reprodução do programa inteiro.

## Pré-requisitos
- Fase 0 concluída (DoD verificado).
- Binários FFmpeg: build **shared** win64 (BtbN ou gyan.dev, variante **LGPL**),
  DLLs (`avcodec-*.dll`, `avformat-*.dll`, `avutil-*.dll`, `swscale-*.dll`,
  `swresample-*.dll`) em `third_party/ffmpeg/`, copiadas para o output via csproj
  (`Content` + `CopyToOutputDirectory`). Registrar a licença em `THIRD-PARTY.md`.
- NuGet no projeto Media: `FFmpeg.AutoGen` (major compatível com as DLLs baixadas).

## Entregáveis
1. `MediaDecoder` (wrapper FFmpeg; nenhum tipo do FFmpeg vaza para fora).
2. `FrameQueue` + `VideoFileSource : IPlayableSource`.
3. `ClipPlayer` (playhead, estados, play modes básicos).
4. `CompositionRenderer` real (render target da composição + passe de saída).
5. UI temporária de teste: abrir arquivo, play/pause, slider de seek, velocidade.

---

## Tarefas

### T1.1 — Bootstrap do FFmpeg
- `FFmpegBootstrap.Initialize()`: setar `ffmpeg.RootPath` para a pasta das DLLs,
  registrar callback de log do FFmpeg → Serilog (nível warning+).
- Teste de fumaça no startup: logar `av_version_info()`.

### T1.2 — MediaDecoder (demux + decode + conversão)
`src/VideoWallRW.Media/Decoding/MediaDecoder.cs` — classe `unsafe`, `IDisposable`:

```csharp
public sealed unsafe class MediaDecoder : IDisposable
{
    public static MediaDecoder Open(string path);   // lança MediaOpenException com causa legível
    public double Duration { get; }
    public int Width { get; } public int Height { get; }
    public double FrameRate { get; }
    public bool TryDecodeNext(DecodedFrame target); // false = EOF
    public void Seek(double seconds);
}
public sealed class DecodedFrame          // buffer BGRA reutilizável (pool)
{
    public byte[] Pixels; public int Stride; public double PtsSeconds;
}
```

Fluxo interno (ordem canônica FFmpeg):
1. `avformat_open_input` → `avformat_find_stream_info` →
   `av_find_best_stream(AVMEDIA_TYPE_VIDEO)`.
2. `avcodec_alloc_context3` + `avcodec_parameters_to_context` +
   `thread_count = 0` (auto) + `avcodec_open2`.
3. Loop: `av_read_frame` (ignorar pacotes de outras streams) →
   `avcodec_send_packet` → `avcodec_receive_frame`.
4. PTS em segundos: `frame->best_effort_timestamp * av_q2d(stream->time_base)`.
5. `sws_getContext` → `sws_scale` para BGRA no buffer do `DecodedFrame`
   (contexto sws cacheado; recriar só se formato mudar).
6. EOF: `avcodec_send_packet(null)` e drenar frames restantes antes de reportar false.
7. `Seek`: `av_seek_frame(..., AVSEEK_FLAG_BACKWARD)` + `avcodec_flush_buffers`
   + decodificar e descartar até `pts >= alvo` (seek preciso).

Todo `av_*_free`/`_close` no `Dispose` (e no finalizer com log de aviso — indica
vazamento de uso).

### T1.3 — FrameQueue e thread de decodificação
`src/VideoWallRW.Media/Decoding/FrameQueue.cs`:
- Fila limitada de `DecodedFrame` (capacidade 6) + pool de buffers (evitar GC).
- Produtor (decode thread) bloqueia quando cheia; consumidor (render thread) pega
  **o frame de maior PTS ≤ playhead** e devolve os anteriores ao pool.

`VideoDecodeWorker`: thread por clip ativo. Estados: decodificando, aguardando
fila, seek pendente (comando com generation counter — descartar frames da
generation antiga), EOF→loop (seek para o início/in-point mantendo a fila quente).

### T1.4 — VideoFileSource (upload para GPU)
`src/VideoWallRW.Media/Sources/VideoFileSource.cs : IPlayableSource`:
- Duas texturas `Usage.Dynamic` BGRA (double buffer) + SRVs.
- `AcquireFrame(playhead)`: consultar a FrameQueue; se há frame novo elegível,
  `Map(WriteDiscard)` + copiar **linha a linha respeitando `RowPitch`** + `Unmap`,
  alternar buffer; senão retornar a SRV anterior (é isso que faz a pausa segurar
  o frame de graça).
- `EndReached` disparado no fim natural quando o play mode não é loop (fase 3).

### T1.5 — ClipPlayer (relógio e transporte)
`src/VideoWallRW.Engine/Playback/ClipPlayer.cs`:

```csharp
public sealed class ClipPlayer : IDisposable
{
    public IPlayableSource Source { get; }
    public PlaybackState State { get; }    // Playing | Paused
    public double Playhead { get; }        // segundos no clip
    public double Speed { get; set; }      // 0.1 .. 4.0
    public void Tick(double dt);           // render thread: if (Playing) Playhead += dt * Speed
    public void Play(); public void Pause(); public void Seek(double s);
}
```

- **Pausa = `Tick` não avança o playhead** → `AcquireFrame` devolve o mesmo frame
  → a tela congela. Nenhum caso especial no render.
- Loop nesta fase: playhead ≥ duration → playhead -= duration (o worker já fez o
  seek da fila em paralelo).

### T1.6 — CompositionRenderer + passe de saída
`src/VideoWallRW.Engine/Rendering/CompositionRenderer.cs`:
- Render target da composição: textura BGRA `RenderTarget|ShaderResource`,
  tamanho da composição (padrão inicial 1920×1080, configurável).
- Passe 1: clear preto → desenhar o quad do clip com escala **Fit** (letterbox) —
  a matemática completa de resize modes vem na fase 2.
- Passe 2 (por saída): desenhar a textura da composição esticada no backbuffer
  (slices de verdade vêm na fase 4).
- Sampler linear clamp; shaders `Composite.hlsl` (VS quad transformado por
  matriz em cbuffer, PS textura × opacity).

### T1.7 — Comandos e UI de teste
- Novos `EngineCommand`: `LoadTestClip(path)`, `PlayClip`, `PauseClip`,
  `SeekClip(seconds)`, `SetClipSpeed(x)`.
- Snapshot para UI: playhead, duração, estado, fps de decode.
- UI: botão "Abrir vídeo...", play/pause, slider de seek (arrastar → comando;
  atualização vinda do snapshot **não** reentra comando — flag de supressão),
  combo de velocidade (0.5/1/2×).

---

## Armadilhas conhecidas
- **Nunca** chamar FFmpeg da render thread — abrir arquivo pode levar segundos
  (rede!). `Open` roda na decode thread; o clip fica `Arming` até o 1º frame.
- `best_effort_timestamp` pode ser `AV_NOPTS_VALUE` → estimar por frame anterior
  + 1/fps.
- Vídeos VFR: nunca assumir intervalo fixo; sempre dirigir por PTS.
- Stride: `sws_scale` pode usar stride ≠ width*4; o upload respeita os dois pitches.
- Seek em H.264 sem `AVSEEK_FLAG_BACKWARD` cai depois do alvo → tela congelada.
- Devolver frames ao pool sempre — qualquer caminho de erro que "esquece" um
  buffer vira leak sob soak test.

## DoD — verificação manual
1. Abrir um MP4 H.264 1080p30: reproduz nas saídas com A/V suave (sem áudio ainda).
2. Pausar: frame congela exato; retomar: continua do ponto (sem pulo).
3. Seek para qualquer ponto do slider: imagem correta em < 300ms.
4. Loop: fronteira sem frame preto nem congelamento perceptível.
5. Velocidade 2× e 0.5× corretas (cronometrar 10s de conteúdo).
6. **Soak**: 1h em loop → memória do processo estável (variação < 50MB).
7. Arquivo corrompido/inexistente: clip em erro com mensagem, engine segue vivo.
