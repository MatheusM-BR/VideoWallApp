# VideoWallRW — Arquitetura e Plano de Desenvolvimento

> Media server estilo Resolume Arena: grade de layers × colunas, fontes variadas
> (arquivos, NDI, placas de captura), saída avançada multi-tela com transformações.
> Sem efeitos pesados — foco em **rodar conteúdo com confiabilidade**.
>
> Decisões tomadas: **C# / .NET + Direct3D 11**, perfil híbrido (operação ao vivo
> **e** instalação fixa), fontes essenciais da v1: **arquivos + NDI + placa de captura**.

---

## 1. Princípios de projeto

1. **O engine nunca depende da UI.** O render continua a 60fps mesmo com a UI travada,
   redimensionando ou fechada. UI e engine conversam por fila de comandos.
2. **Tudo que toca pixel roda na GPU.** CPU só decodifica e move dados; composição,
   blend, escala, rotação e recorte de slices são shaders D3D11.
3. **Nunca mostrar frame preto por acidente.** Trocas de clip fazem preroll: o novo
   clip só entra no ar quando o primeiro frame dele já está decodificado.
4. **Estado é serializável.** O projeto inteiro (grade, saídas, transformações) é um
   JSON versionado. Autosave + recuperação de crash desde cedo.
5. **Falhar de forma isolada.** Um arquivo corrompido ou NDI que caiu não derruba o
   engine — o clip fica marcado com erro e o resto continua.

---

## 2. Modelo de domínio

```
Composition
├── settings: largura × altura (ex: 3840×1080), fps master, duração padrão de imagem
├── masterFader, blackout
├── Layers[] (linhas, de baixo pra cima)
│   ├── nome, ordem, bypass, solo
│   ├── blendMode (Normal | Add | Multiply | Screen)
│   ├── opacity (0..1)
│   ├── transform (posição, escala, rotação, âncora)
│   ├── transitionDuration (crossfade ao trocar de clip; 0 = corte seco)
│   ├── resizeModeDefault (Fit | Fill | Stretch | Original)
│   └── Clips[] (um por coluna; célula pode estar vazia)
│       ├── source: SourceDescriptor
│       ├── playMode: Loop | PingPong | OnceHold | OnceClear
│       ├── speed (0.1x .. 4x), inPoint, outPoint
│       ├── imageDuration (imagens; herda da composition se nulo)
│       ├── resizeMode (herda da layer se nulo) + transform override
│       ├── volume, mute
│       ├── autopilot: Nada | PróximoClip | ClipAnterior | Aleatório | ColunaEspecífica
│       └── thumbnail, nome, cor de etiqueta
├── Columns[] (cabeçalhos; disparar coluna = disparar o clip daquela coluna em toda layer)
├── Decks[] (bancos de grades — abas; v2)
└── OutputSetup (ver §5)
```

**SourceDescriptor** (união discriminada):

| Tipo            | Dados                                   | Fase |
|-----------------|------------------------------------------|------|
| `FileVideo`     | caminho, hash p/ relink                  | v1   |
| `FileImage`     | caminho                                  | v1   |
| `Ndi`           | nome da fonte na rede, bandwidth mode    | v1   |
| `CaptureDevice` | id do dispositivo, formato/resolução/fps | v1   |
| `SolidColor`    | cor RGBA                                 | v1 (barato, ótimo p/ testes) |
| `TestPattern`   | tipo (barras, grid, identify)            | v1   |
| `DesktopCapture`| monitor/janela, região opcional          | v2   |
| `Spout`         | nome do sender                           | v2   |

---

## 3. Arquitetura do engine

### 3.1 Threads

```
┌─────────────┐  Channel<EngineCommand>   ┌──────────────────┐
│  UI (WPF)   │ ─────────────────────────▶│  Render Thread    │──▶ swapchains (1 por saída)
│  dispatcher │ ◀─────────────────────────│  (dona do D3D11)  │
└─────────────┘  eventos/snapshots         └───────┬──────────┘
                 (playheads, fps, thumbs)          │ lê últimas texturas
                                          ┌────────┴─────────────────────┐
                                          │ Decode Threads (1 por clip   │
                                          │ ativo; demux+decode FFmpeg)  │
                                          │ Capture Threads (NDI, MF)    │
                                          │ Audio Thread (WASAPI/NAudio) │
                                          └──────────────────────────────┘
```

- **UI → engine**: apenas comandos imutáveis (`TriggerClip`, `SetLayerOpacity`,
  `PauseClip`, `LoadProject`...) por `Channel<T>`. Nada de lock compartilhado no
  estado de render.
- **Engine → UI**: snapshot leve por frame de UI (~30Hz): playheads, clip ativo por
  layer, fps, frames perdidos. Thumbnails e preview via textura compartilhada.
- **Fontes → render**: cada fonte publica "última textura pronta" (double/triple
  buffer com troca atômica). O render thread nunca espera fonte nenhuma — se não
  chegou frame novo, repete o anterior.

### 3.2 Loop de render (por frame)

1. Processar fila de comandos.
2. Avançar relógio: `playhead += dt × speed` para cada clip tocando
   (**pausa = playhead congela → mesmo frame permanece na tela**).
3. Para cada layer visível (de baixo pra cima): pegar textura atual do clip ativo,
   desenhar quad no render target da composição com blend state, opacity e matriz
   de transform (resize mode calculado aqui). Durante crossfade, desenha clip que
   sai + clip que entra com fades cruzados.
4. Passe de saída: para cada janela de output, para cada slice: amostrar o subretângulo
   `inputRect` da textura da composição e desenhar no `outputRect` do backbuffer com
   rotação/flip.
5. `Present` de todas as swapchains (vsync na principal; secundárias com
   `ALLOW_TEARING` ou threads de present dedicadas — ver riscos §10).
6. Publicar textura da composição para o preview da UI (downscale, ~30Hz basta).

### 3.3 Relógio e seleção de frame

- Relógio master = `QueryPerformanceCounter` (via `Stopwatch`).
- Cada clip de vídeo tem playhead próprio em segundos. O render escolhe na fila de
  frames decodificados o de maior PTS ≤ playhead e descarta os anteriores.
- Vídeo com áudio: o áudio segue o playhead do vídeo com buffer curto; drift é
  corrigido com micro-resample (suficiente para este uso — não somos DAW).
- Imagens: `playhead` conta até `imageDuration` → dispara ação do autopilot.

### 3.4 Decodificação (FFmpeg)

- **Bindings**: FFmpeg.AutoGen (ou Sdcb.FFmpeg) encapsulado num wrapper nosso
  (`MediaDecoder`) — o resto do código nunca vê structs do FFmpeg.
- **Hardware**: D3D11VA (funciona em NVIDIA/AMD/Intel) → frame NV12 já em textura
  D3D11 → conversão NV12→RGBA em pixel shader. Fallback software: `sws_scale` → upload.
- **Fila limitada** (4–8 frames) por clip; decode thread bloqueia quando cheia.
- **Preroll no disparo**: abrir, seek pro in-point, decodificar 1º frame, só então
  trocar o clip no ar (elimina flash preto; latência alvo < 100ms).
- **Loop**: no EOF, seek pro in-point mantendo a fila quente (loop sem soluço).
- **HAP** (fase 6): FFmpeg entrega blocos DXT prontos → upload direto como textura
  comprimida BC1/BC3. Custo de CPU quase nulo — é o caminho para muitos layers 4K
  simultâneos, como o DXV do Resolume.

### 3.5 Fontes ao vivo

- **NDI**: SDK oficial (Processing.NDI.Lib) via P/Invoke. Uma thread de recv por
  fonte; frames BGRA/UYVY → conversão em shader. Reconexão automática com backoff;
  descoberta de fontes (NDI Find) alimenta o navegador de fontes da UI.
- **Placa de captura**: Media Foundation (`IMFSourceReader`) via Vortice.MediaFoundation;
  enumeração de dispositivos + formatos suportados; fallback DirectShowLib para
  drivers antigos. DeckLink (SDK Blackmagic) fica para fase posterior.
- **Desktop (v2)**: Windows.Graphics.Capture → a textura já nasce na GPU (custo ~zero).

---

## 4. Semântica de reprodução (requisitos do usuário)

| Requisito                            | Implementação                                        |
|--------------------------------------|------------------------------------------------------|
| Pausar e segurar o mesmo frame       | playhead congela; render repete a última textura     |
| Tempo de exibição de imagens         | `imageDuration` por clip, default na composition     |
| Ajuste de proporção automático       | resize modes Fit/Fill/Stretch/Original, default por layer, override por clip |
| Rodar sem operador                   | autopilot por clip + sequenciador de colunas + agendador (§8) |
| Atalhos de comando                   | keymap JSON configurável (§7)                        |

---

## 5. Saída avançada (Advanced Output)

Duas camadas de transformação, como no Resolume:

- **Input transformation** (slice → composição): qual retângulo da composição o
  slice enxerga (`inputRect`). É aqui que se resolve disposição de telas, ordem de
  cabos e compensação de moldura (bezel).
- **Output transformation** (slice → dispositivo): onde o slice desenha no
  backbuffer (`outputRect`), com rotação 0/90/180/270 (telas em pé), flip H/V e,
  em fase futura, corner-pin (warp) e edge blend para projetores.

```
OutputSetup
├── Screens[] (1 por monitor físico; futuramente NDI out / virtual)
│   ├── dispositivo (id do monitor, resolução, posição no desktop)
│   ├── colorCorrection (brilho, contraste, gamma — casar telas diferentes)
│   └── Slices[]
│       ├── inputRect  (px no espaço da composição)
│       ├── outputRect (px no dispositivo)
│       └── rotation, flipH, flipV
└── presets de output salvos separados do projeto (reusar entre projetos)
```

Janelas de saída são **borderless fullscreen** (não exclusive-fullscreen — mais
estável com multi-monitor e overlays do Windows). Modo **Identify**: cartão de
teste com nome/número/resolução em cada saída.

Editor visual: canvas com os slices arrastáveis sobre um wireframe da composição
(lado input) e dos monitores (lado output), com snap, zoom e campos numéricos.

---

## 6. UI (WPF, padrão MVVM)

```
┌──────────────────────────────────────────────────────────────┐
│ Menu │ Master fader │ Blackout │ FPS/drops │ [Output ▾]       │
├──────┬───────┬───────┬───────┬───────┬──────────────────────┤
│      │ Col 1 │ Col 2 │ Col 3 │ Col 4 │  ◄ cabeçalhos disparam a coluna
│ Lyr3 │ [clip]│ [clip]│  ...  │       │                       │
│ Lyr2 │ [clip]│       │ [clip]│       │  ◄ faixa da layer: nome, blend,
│ Lyr1 │ [clip]│ [clip]│       │ [clip]│    opacity, bypass/solo, clear (X)
├──────┴───────┴───────┴───────┴───────┴──────────────────────┤
│ Preview (clip selecionado) │ Programa (composição no ar)      │
├──────────────────────────────────────────────────────────────┤
│ Abas: Clip │ Layer │ Composition │ Output │ Fontes (NDI/captura) │
└──────────────────────────────────────────────────────────────┘
```

- Célula do clip: thumbnail, nome, barra de progresso do playhead, ícone do tipo
  de fonte, borda destacada quando no ar.
- Clique = dispara; botão direito = preview/propriedades; arrastar do explorador
  de arquivos = importa (multi-seleção preenche colunas em sequência).
- Preview do programa: textura compartilhada do engine → `D3DImage` (evita problema
  de airspace do WPF); thumbnails via `WriteableBitmap` em taxa baixa.

---

## 7. Controle

**Atalhos (keymap JSON editável, defaults):**

| Tecla            | Ação                                        |
|------------------|---------------------------------------------|
| `1..9, 0`        | Disparar colunas 1–10                        |
| Setas + `Enter`  | Navegar grade / disparar clip selecionado    |
| `Espaço`         | Pausar/retomar clip da layer selecionada     |
| `Ctrl+Espaço`    | Pausar/retomar TUDO (freeze geral)           |
| `X` / `Ctrl+X`   | Limpar layer selecionada / limpar tudo       |
| `B` ou `Esc`     | Blackout master (com fade configurável)      |
| `F`              | Ligar/desligar janelas de saída (pânico)     |
| `Ctrl+S / O / N` | Salvar / abrir / novo projeto                |

**Fases futuras**: MIDI (DryWetMIDI) com modo "MIDI learn", OSC (entrada UDP),
controle remoto via navegador no celular (WebSocket espelhando a grade).

---

## 8. Modo instalação fixa

- **Autostart**: iniciar com o Windows, carregar último projeto, ativar saídas e
  disparar coluna inicial sem interação.
- **Watchdog**: processo supervisor leve; se o engine parar de responder ao
  heartbeat, reinicia e retoma o estado salvo.
- **Sequenciador de colunas**: playlist (coluna 1 por 30s → coluna 2 por 1min → loop).
- **Agendador**: por relógio/dia da semana ("18h dispara coluna Noite").
- **Watch folder**: pasta monitorada vira coluna automaticamente (cliente joga
  arquivo na pasta e o conteúdo entra na rotação).

---

## 9. Persistência

- Projeto = JSON versionado (`schemaVersion`) via System.Text.Json.
- Caminhos de mídia relativos ao projeto, com absoluto de fallback + hash do
  arquivo para **relink** de mídia movida.
- Autosave a cada 2 min + no fechamento; cópia `.bak`; recuperação pós-crash.
- Cache de thumbnails em `%LocalAppData%\VideoWallRW\thumbs` (chave = hash+mtime).
- Presets de output separados do projeto (a instalação física não muda por show).

---

## 10. Riscos técnicos conhecidos

| Risco | Mitigação |
|-------|-----------|
| Vsync com monitores de taxas diferentes trava o frame loop | Present com `ALLOW_TEARING` nas secundárias ou thread de present por saída; medir cedo (fase 0) |
| Limite de sessões NVDEC / pool de decode HW | Cap de decodificadores HW simultâneos + fallback software transparente |
| Airspace do WPF com conteúdo D3D | Preview via `D3DImage` (surface compartilhada), não `HwndHost` |
| Muitos vídeos 4K simultâneos estouram CPU | Caminho HAP (fase 6) + conversor em lote embutido |
| NDI saturando a rede | Suporte a NDI|HX e seleção de bandwidth por fonte |
| Placas de captura com drivers problemáticos | MF primeiro, DirectShow como fallback; timeout + re-open automático |

Fora de escopo da v1: HDR, 10-bit, edge blend, warping mesh, efeitos GLSL/ISF, BPM sync.

---

## 11. Stack técnica

| Camada | Escolha |
|--------|---------|
| Runtime | .NET 8 LTS, C# 12 |
| GPU | Vortice.Windows (D3D11 + DXGI), HLSL |
| UI | WPF (MVVM; CommunityToolkit.Mvvm) |
| Decode | FFmpeg 7 via FFmpeg.AutoGen (wrapper próprio), D3D11VA |
| NDI | NDI SDK 6 via P/Invoke |
| Captura | Vortice.MediaFoundation (+ DirectShowLib fallback) |
| Áudio | NAudio (WASAPI) |
| Persistência | System.Text.Json |
| Logs | Serilog (arquivo rotativo + painel de diagnóstico) |

**Estrutura da solução:**

```
VideoWallRW.sln
├── src/VideoWallRW.Core      → modelo de domínio, projeto/JSON, comandos (sem deps de GPU/UI)
├── src/VideoWallRW.Engine    → D3D11, render loop, composição, saídas, relógio
├── src/VideoWallRW.Media     → FFmpeg wrapper, decoders, thumbnails, áudio
├── src/VideoWallRW.Sources   → NDI, Media Foundation, (desktop, Spout futuramente)
├── src/VideoWallRW.App       → WPF: grade, painéis, editor de output, atalhos
└── tests/                    → testes do Core (modelo, serialização, relógio)
```

---

## 12. Roadmap por fases

Cada fase termina com um critério verificável ("DoD"). Não avançar com DoD quebrado.

| Fase | Entrega | DoD |
|------|---------|-----|
| **0. Fundação** | Solução .NET, device D3D11, janela de saída borderless em monitor secundário, frame loop, FPS HUD | Quad animado a 60fps estáveis em 2 monitores simultâneos |
| **1. Player** | FFmpeg (decode SW) → textura → composição → saída; pause/seek/loop/speed | 1 vídeo em loop 1h sem vazar memória; pause segura o frame |
| **2. Grade** | Layers × colunas na UI, trigger por clique, blend/opacity, imagens com duração, resize modes, thumbnails | 3 layers compostas com blend na saída, trocando clips sem frame preto |
| **3. Transporte** | Painéis clip/layer/composition, in/out points, play modes, crossfade por layer, autopilot básico, atalhos principais | Operável só pelo teclado; autopilot roda uma coluna sozinho |
| **4. Advanced Output** | Editor de slices, input/output transform, rotação, identify, presets de output | Composição 3840×1080 fatiada em 2 monitores com bezel compensado |
| **5. Fontes ao vivo** | NDI (descoberta + recv + reconexão) e placa de captura (MF, enumeração de formatos) | NDI e HDMI ao vivo na grade, sobrevivendo a desconexão/reconexão |
| **6. Performance** | D3D11VA, HAP, preroll de disparo, cap de decoders, telemetria de drops | 4 vídeos 1080p60 + 1 NDI simultâneos sem drops em GPU mediana |
| **7. Operação** | Save/load, autosave, relink, snapshots de cena, master fader/blackout, keymap editável | Fechar e reabrir projeto restaura exatamente o estado |
| **8. Instalação** | Autostart, retomar estado, watchdog, sequenciador de colunas, agendador, watch folder | PC reinicia sozinho e o conteúdo volta ao ar sem interação |
| **9. Extras** | Desktop capture, Spout, MIDI/OSC, correção de cor por saída, remote web | — |
| **10. Módulo Agenda** | Captura de banner/catálogo de eventos do programaleiloes.com + editor de imagem embutido (crop/proporção/pintura) — paralelizável após a fase 3 | Banner do evento do dia editado e no ar sem sair do programa |

---

## 13. Ideias adicionais de funcionamento

- **Snapshots/cenas**: salvar a foto inteira da grade (clip ativo por layer +
  opacidades + fader) e disparar como preset com um atalho.
- **Preview antes do ar**: botão direito manda pro monitor de preview; `Enter`
  promove pro programa.
- **Cartão de identificação de saídas** com nome/número gigante em cada tela.
- **HUD de performance** (fps, drops, VRAM, decoders ativos) com aviso visual
  quando dropar frame no ar.
- **Correção de cor por saída** para casar TVs/painéis de fabricantes diferentes.
- **Conversor em lote para HAP** embutido (fila de transcodificação com ffmpeg).
- **Relatório de mídia**: ao importar, avisar codec problemático (H.265 10-bit,
  VFR...) e oferecer conversão.
- **Ken Burns** para imagens (zoom/pan lento) — dá vida a fotos paradas.
- **Volume por clip + mute global**; áudio segue o clip que está no ar.
- **Decks (bancos)** como abas para trocar de repertório sem fechar projeto.
- **Etiquetas coloridas e busca** na grade para shows grandes.
- **Modo ensaio**: saídas renderizando numa janela simulada (trabalhar sem o
  hardware do show conectado).
