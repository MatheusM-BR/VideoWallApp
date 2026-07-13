# Fase 2 — Grade: layers × colunas

## Objetivo
O coração do programa: modelo de domínio completo no `Core`, composição de
múltiplas layers com blend/opacity na GPU, disparo de clips por clique, imagens
com duração, resize modes e thumbnails. Ao final, o programa já "parece" um
Resolume mínimo.

## Pré-requisitos
- Fase 1 concluída (DoD verificado).
- NuGet no Engine: `Vortice.WIC` (carregamento de imagens) — se a versão do
  Vortice já expõe WIC pelo pacote principal, usar de lá.

## Entregáveis
1. Modelo de domínio no `Core` (Composition/Layer/Clip/SourceDescriptor).
2. `LayerRenderer` com blend modes e opacity; composição N layers.
3. `ImageFileSource` com duração; resize modes completos.
4. Grade na UI (disparo por clique, célula com thumbnail e progresso).
5. Faixa da layer: nome, blend, opacity, bypass, solo, clear.

---

## Tarefas

### T2.1 — Modelo de domínio (Core)
`src/VideoWallRW.Core/Model/`:

```csharp
public sealed class Composition {
    public int Width = 1920, Height = 1080;
    public double DefaultImageDuration = 10.0;    // segundos
    public double MasterOpacity = 1.0;
    public List<Layer> Layers = [];               // índice 0 = base (desenha primeiro)
    public int ColumnCount = 8;
}
public sealed class Layer {
    public Guid Id; public string Name = "";
    public BlendMode Blend = BlendMode.Normal;    // Normal | Add | Multiply | Screen
    public double Opacity = 1.0;
    public bool Bypass, Solo;
    public double TransitionSeconds = 0.0;        // crossfade na troca de clip (fase 3 usa)
    public ResizeMode DefaultResize = ResizeMode.Fit;
    public Clip?[] Clips = [];                    // tamanho = ColumnCount; null = célula vazia
}
public sealed class Clip {
    public Guid Id; public string Name = "";
    public SourceDescriptor Source;
    public PlayMode PlayMode = PlayMode.Loop;     // Loop | PingPong | OnceHold | OnceClear
    public double Speed = 1.0;
    public double? InPoint, OutPoint, ImageDuration;
    public ResizeMode? Resize;                    // null = herda da layer
    public double Volume = 1.0; public bool Mute;
    public AutopilotAction Autopilot = AutopilotAction.None;
}
public abstract record SourceDescriptor;          // polimorfismo p/ JSON na fase 7
public sealed record FileVideoSource(string Path) : SourceDescriptor;
public sealed record FileImageSource(string Path) : SourceDescriptor;
public sealed record SolidColorSource(uint Rgba) : SourceDescriptor;
// Ndi / CaptureDevice entram na fase 5; TestPattern na fase 4.
```

- Testes unitários: invariantes (Clips.Length == ColumnCount ao adicionar
  coluna/layer), clonagem, defaults.

### T2.2 — Comandos de grade
`TriggerClip(layerId, column)`, `TriggerColumn(column)`, `ClearLayer(layerId)`,
`ClearAll`, `SetLayerOpacity(layerId, value)`, `SetLayerBlend(layerId, mode)`,
`SetLayerBypass/Solo`, `UpdateClip(clipSnapshot)` (propriedades editadas na UI).
O engine mantém a **cópia autoritativa** do modelo; a UI edita via comandos e
recebe snapshots (nunca há duas fontes de verdade).

### T2.3 — LayerRenderer e blend modes
`src/VideoWallRW.Engine/Rendering/LayerRenderer.cs` — um por layer, dono do
`ClipPlayer` ativo (e do que está saindo, quando houver crossfade na fase 3).

Blend na composição (opacity **pré-multiplicada no pixel shader**:
`rgb *= a * opacity; a *= opacity`):

| Modo | SrcBlend | DestBlend | Observação |
|------|----------|-----------|------------|
| Normal | One | InvSrcAlpha | premultiplied alpha padrão |
| Add | One | One | |
| Screen | One | InvSrcColor | s + d·(1−s) |
| Multiply | DestColor | Zero | no shader, `rgb = lerp(1.0, rgb, opacity)` para o fade tender ao branco (identidade da multiplicação) |

- Um `ID3D11BlendState` por modo, criados uma vez.
- Ordem de desenho: Layers[0] primeiro (base), últimas por cima.
- `Bypass` pula a layer; `Solo` em qualquer layer → só desenha as em solo.

### T2.4 — Resize modes (matemática de proporção)
`src/VideoWallRW.Core/Math/ResizeMath.cs` (testável sem GPU):

```csharp
// Retorna retângulo destino em px da composição para um source w×h
public static RectF Compute(ResizeMode mode, float srcW, float srcH, float compW, float compH);
```

- `Fit`: `s = min(compW/srcW, compH/srcH)`, centralizado (letterbox).
- `Fill`: `s = max(...)`, centralizado (crop) — **este é o "ajuste automático"
  padrão para video wall**.
- `Stretch`: deforma para compW×compH.
- `Original`: 1:1 pixels, centralizado.
- Unit tests com casos 16:9→ultrawide, 9:16→16:9, src maior que comp, etc.
- O `LayerRenderer` converte o RectF em matriz do quad (cbuffer do VS).

### T2.5 — ImageFileSource
`src/VideoWallRW.Media/Sources/ImageFileSource.cs : IPlayableSource`:
- Decodificar via WIC: `IWICImagingFactory` → decoder → `FormatConverter` para
  `32bppPBGRA` → textura **imutável** (uma só, sem double buffer).
- Carregamento em thread de trabalho (arquivo grande/rede não trava o disparo);
  estado `Arming` até pronto.
- `Duration` = `clip.ImageDuration ?? composition.DefaultImageDuration`;
  `EndReached` quando o playhead do ClipPlayer atinge a duração (pausa **congela
  a contagem** — imagem pausada fica para sempre).
- Formatos mínimos: PNG (com alpha), JPEG, BMP; GIF entra como imagem estática
  (1º frame) — GIF animado no backlog.

### T2.6 — Disparo de clip (troca sem frame preto)
Fluxo do `TriggerClip` dentro do engine:
1. Criar fonte + player do novo clip em estado `Arming` (decode/carga em background).
2. Enquanto arma, **o clip anterior continua no ar**.
3. No primeiro `AcquireFrame` não-nulo do novo → troca atômica (dispose do
   antigo agendado para o fim do frame).
4. Timeout de 2s armando → clip marcado `Error`, anterior permanece.
- Disparar o mesmo clip de novo = retrigger (volta ao início/in-point).
- `TriggerColumn(c)`: aplica o fluxo acima em toda layer que tem clip na coluna c.

### T2.7 — Thumbnails
`src/VideoWallRW.Media/Thumbnails/ThumbnailService.cs`:
- Vídeo: abrir `MediaDecoder`, seek para 10% da duração, decodificar 1 frame,
  reduzir para altura 90px, salvar PNG em
  `%LocalAppData%\VideoWallRW\thumbs\{xxhash(path+mtime)}.png`.
- Imagem: WIC com downscale direto.
- Fila de trabalho com 2 threads; UI recebe o caminho pronto via evento e carrega
  `BitmapImage` (UI nunca espera thumbnail).

### T2.8 — UI da grade
- `ItemsControl` vertical (layers em ordem **inversa** — topo da lista = layer de
  cima) contendo `ItemsControl` horizontal de células.
- Célula: thumbnail, nome, barra fina de progresso do playhead (do snapshot),
  borda acesa quando no ar, ícone do tipo de fonte, célula vazia aceita clique
  direito → "Adicionar arquivo...".
- Cabeçalhos de coluna clicáveis (TriggerColumn).
- Faixa da layer (esquerda da linha): nome editável, combo blend, slider opacity
  (0–100%), toggles B (bypass) / S (solo), botão X (clear).
- **Drag & drop do Explorer**: soltar N arquivos numa célula preenche células
  seguintes da mesma layer em sequência. Soltar sobre área vazia abaixo da última
  layer cria layer nova.
- Toolbar: "Adicionar layer", "Adicionar coluna", tamanho da composição
  (diálogo simples), master opacity (slider).

---

## Armadilhas conhecidas
- Solo/bypass são estado do **engine** — a UI só reflete o snapshot (senão diverge).
- Dispose do player antigo na troca: agendar para depois do Present (o frame
  corrente ainda pode estar usando a SRV dele).
- WIC `FormatConverter` é obrigatório — JPEGs chegam como 24bpp e PNGs paletados
  existem; sempre converter para 32bppPBGRA.
- Muitos clips armando ao mesmo tempo (TriggerColumn com 5 layers): limitar
  aberturas simultâneas de decoder (semáforo, 4) — resto fila.
- Layer sem clip ativo desenha nada (não desenhar quad preto — quebraria Add/Screen).

## DoD — verificação manual
1. 3 layers (vídeo base + PNG com alpha em Normal + vídeo em Add) compostas
   corretamente na saída.
2. Disparar clips trocando entre colunas: **nunca** aparece frame preto.
3. TriggerColumn troca as 3 layers juntas.
4. Imagem com duração 5s → `EndReached` loga (autopilot real é fase 3); pausar a
   imagem congela a contagem.
5. Resize: mesmo vídeo 16:9 em composição 3840×720 — Fit (barras), Fill (crop),
   Stretch (deforma), Original (1:1) visualmente corretos.
6. Opacity e blend respondem em tempo real sem soluço.
7. Drag & drop de 6 arquivos preenche 6 células com thumbnails aparecendo depois.
