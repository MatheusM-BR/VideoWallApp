# Fase 3 — Transporte, painéis de propriedades, atalhos e autopilot

## Objetivo
O programa vira operável de verdade: painéis de propriedades (Clip / Layer /
Composition), in/out points, play modes completos, crossfade por layer, autopilot
e o sistema de atalhos de teclado configurável. Também entra o áudio básico.

## Pré-requisitos
- Fase 2 concluída (DoD verificado).
- NuGet no Media: `NAudio`.

## Entregáveis
1. Painel inferior com abas Clip / Layer / Composition (binding pela seleção).
2. In/out points, play modes (Loop, PingPong*, OnceHold, OnceClear).
3. Crossfade por layer na troca de clips.
4. Autopilot por clip.
5. `KeymapService` + atalhos padrão; pausa master.
6. Áudio do clip no ar (WASAPI), volume por clip + master, mute.

---

## Tarefas

### T3.1 — Seleção e painéis de propriedades
- Conceito de **célula selecionada** (clique simples seleciona, duplo/enter dispara
  — configurável depois; padrão Resolume: clique dispara, então: clique = dispara
  E seleciona; botão direito = só seleciona + menu).
- Aba Clip: nome, play mode, speed (slider log 0.1–4×), in/out (campos + botões
  "marcar in/out no playhead"), duração de imagem, resize override, volume/mute,
  autopilot.
- Aba Layer: nome, blend, opacity, transition (crossfade) em segundos, resize
  default, bypass/solo.
- Aba Composition: tamanho (largura×altura), duração padrão de imagem, master
  opacity, contagem de colunas.
- Edições geram `UpdateClip`/`UpdateLayer`/`UpdateComposition` → engine aplica →
  snapshot confirma (UI otimista, mas a verdade é do engine).

### T3.2 — In/out points e play modes
No `ClipPlayer` + workers:
- Playhead passa a viver no intervalo `[in, out]` (defaults 0..duration).
- `Loop`: out → seek in (fila quente, já feito na fase 1; agora respeitando in/out).
- `OnceHold`: out → dispara `EndReached`, congela no último frame (estado
  `Playing→Paused` implícito).
- `OnceClear`: out → `EndReached` + layer limpa (com crossfade da layer, se houver).
- `PingPong`: **restrição v1** — vídeo reverso exige decode para trás (caro).
  Implementar: imagens (trivial) e vídeos com `out-in ≤ 10s` via cache de frames
  decodificados em RAM (BGRA 1080p ≈ 8MB/frame → 10s@30 ≈ 2.5GB... **não**:
  limitar a `out-in ≤ 4s` @ ≤1080p, cache ~1GB máx, documentado). Acima disso, UI
  mostra o modo desabilitado com tooltip. Reavaliar na fase 6 (HAP torna viável).

### T3.3 — Crossfade por layer
No `LayerRenderer`:
- `TransitionSeconds > 0`: na troca, manter player antigo + novo; desenhar ambos
  com fades cruzados (fade em opacity pré-multiplicada); ao completar, dispose do
  antigo.
- Transição começa **quando o novo clip está pronto** (depois do Arming — nunca
  crossfade para preto).
- Retrigger do mesmo clip: sem crossfade, corte seco no in-point.

### T3.4 — Autopilot
- `AutopilotAction`: `None | NextClip | PreviousClip | RandomClip | FirstColumn |
  SpecificColumn(n)`.
- Disparado pelo `EndReached` (fim natural: imagem completou duração, vídeo
  OnceHold/OnceClear terminou, ou vídeo em Loop completou N voltas — campo
  "avançar após N loops", default desligado).
- `NextClip`: próxima célula **não-vazia** à direita na mesma layer (wrap).
- Engine-side (funciona sem UI aberta — pré-requisito do modo instalação).

### T3.5 — KeymapService e atalhos
`src/VideoWallRW.App/Input/KeymapService.cs`:
- Mapa ação→gesto em `%AppData%\VideoWallRW\keymap.json`
  (ex.: `{ "TriggerColumn1": "D1", "Blackout": "B", "PanicOutputs": "F" }`),
  criado com defaults na primeira execução; parse com `KeyGestureConverter`.
- Registro central no `PreviewKeyDown` da janela principal (não `KeyBinding` do
  WPF espalhado — precisa funcionar com foco em qualquer painel; suprimir quando
  foco em TextBox).
- Ações padrão (da ARQUITETURA §7): colunas 1–10 (`D1..D0`), navegação por setas
  + `Enter` dispara selecionado, `Espaço` pausa/retoma clip da layer selecionada,
  `Ctrl+Espaço` **pausa master** (congela todos os playheads — comando de engine
  `PauseAll`/`ResumeAll`), `X`/`Ctrl+X` clear layer/tudo, `B` blackout (por ora:
  master opacity → 0 com fade de 0.5s; toggle), `F` liga/desliga janelas de saída,
  `Ctrl+S/O/N` reservados (fase 7).
- Editor de keymap na UI fica para fase 7; por ora o JSON é editável à mão.

### T3.6 — Áudio básico
`src/VideoWallRW.Media/Audio/`:
- No `MediaDecoder`: decodificar também a stream de áudio (quando existir) →
  `swr_convert` para float estéreo 48kHz → fila de samples.
- Saída: NAudio `WasapiOut` (shared mode) com um mixer simples; ganho =
  `clip.Volume × master × (Mute ? 0 : 1)`.
- Sincronização pragmática: buffer alvo de ~100ms; se o áudio adiantar/atrasar
  do playhead além de 150ms, ressincronizar descartando/inserindo silêncio
  (suficiente para telão; não somos DAW).
- **Pausa** = parar de consumir samples (e esvaziar o buffer do device para não
  "cauda de áudio").
- Política: **só o clip no ar toca áudio**; durante crossfade, fade de áudio junto.

---

## Armadilhas conhecidas
- Espaço/Enter/setas conflitam com controles WPF focados — tratar no
  `PreviewKeyDown` da janela e marcar `Handled`, exceto quando foco em edição de
  texto.
- `EndReached` de OnceClear durante crossfade: não iniciar segundo crossfade por
  cima; enfileirar.
- Autopilot em coluna vazia inteira → não fazer nada (não loop infinito).
- WasapiOut precisa ser criado/destruído na mesma thread (thread de áudio dedicada
  com fila de comandos, igual padrão do engine).
- Speed ≠ 1 com áudio: v1 **muta o áudio** quando speed ≠ 1.0 (pitch/timestretch
  é backlog) — documentar na UI (tooltip).

## DoD — verificação manual
1. Operação completa **só pelo teclado**: navegar, disparar, pausar, limpar,
   blackout, colunas 1–8.
2. In/out num vídeo: reproduz somente o trecho, loop no trecho.
3. OnceHold congela no último frame; OnceClear limpa a layer.
4. Layer com transition 1s: trocas fazem crossfade suave; retrigger é corte.
5. Coluna de imagens com autopilot NextClip roda sozinha indefinidamente
   (slideshow) — inclusive com a UI minimizada.
6. `Ctrl+Espaço` congela TUDO (vídeos e contagem de imagens); retoma exato.
7. Áudio do clip no ar com volume/mute funcionando; pausa corta o áudio na hora;
   crossfade cruza o áudio.
