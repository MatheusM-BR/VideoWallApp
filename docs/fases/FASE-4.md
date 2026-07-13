# Fase 4 — Advanced Output: telas, slices e transformações

## Objetivo
Substituir o passe de saída "esticado" por um sistema de **slices** com input
transformation (que pedaço da composição) e output transformation (onde desenha
no monitor), com editor visual, modo identify e correção de cor por saída.
É o que transforma o player num media server de video wall.

## Pré-requisitos
- Fase 3 concluída (DoD verificado).
- NuGet no Engine: `Vortice.Direct2D1` + `Vortice.DirectWrite` (texto do identify).

## Entregáveis
1. Modelo `OutputSetup / Screen / Slice` no Core (+ testes da matemática).
2. Passe de saída por slices no engine (rotação, flip, cor por tela).
3. Editor visual de output (janela própria).
4. Modo Identify e test patterns.
5. Presets de output salvos separados do projeto.

---

## Tarefas

### T4.1 — Modelo (Core)
```csharp
public sealed class OutputSetup {
    public List<Screen> Screens = [];
}
public sealed class Screen {
    public Guid Id; public string Name = "";
    public string MonitorDeviceName = "";      // "\\.\DISPLAY2" (id estável da fase 0)
    public bool Enabled;
    public ColorCorrection Color = new();      // brightness, contrast, gamma (por canal futuro)
    public List<Slice> Slices = [];
}
public sealed class Slice {
    public Guid Id; public string Name = "";
    public RectF InputRect;                    // px no espaço da composição
    public RectF OutputRect;                   // px no monitor (origem = canto do monitor)
    public Rotation Rotation;                  // R0 | R90 | R180 | R270
    public bool FlipH, FlipV;
}
```
- `SliceMath` no Core: dado slice + tamanhos, produzir (a) matriz do quad no
  espaço NDC do backbuffer e (b) retângulo UV na textura da composição —
  **testável sem GPU** (casos: rotação 90 com aspecto trocado, flip, out of
  bounds parcial).

### T4.2 — Passe de saída por slices
No engine:
- Para cada `Screen` habilitada → janela de saída (reusar OutputWindow da fase 0,
  agora criada a partir do `MonitorDeviceName`).
- Para cada `Slice`: desenhar quad com UV = `InputRect` normalizado e posição =
  `OutputRect` transformado (rotação/flip na matriz, não no UV — mantém filtro
  linear correto).
- Fora do slice: preto. Slices podem se sobrepor (desenho na ordem da lista).
- **Correção de cor** no pixel shader de saída (por tela):
  `c = pow(saturate(c * contrast + brightness), 1/gamma)` — constantes no cbuffer.
- Master fader/blackout aplicado aqui (multiplicador global) — move o que a fase
  3 fazia via master opacity para o lugar certo (blackout escurece a SAÍDA,
  preview da composição continua visível para o operador).

### T4.3 — Identify e test patterns
- `TestPatternSource : IFrameSource` (Engine): barras SMPTE simplificadas, grid
  de convergência, e **identify**: fundo colorido único por tela + texto gigante
  (nome da tela, device, resolução, índice do slice).
- Texto: renderizar uma vez com Direct2D/DirectWrite sobre textura
  (render target D2D em textura D3D11 compartilhada) — não por frame.
- Comando `SetIdentify(bool)`: sobrepõe o identify em TODAS as saídas por cima
  dos slices (não mexe no estado da grade).

### T4.4 — Editor visual de output
Janela separada (`OutputSetupWindow`), dois painéis lado a lado:
- **Painel Input**: wireframe da composição (retângulo com miniatura ao vivo da
  composição como fundo, ~10fps) com os `InputRect` de cada slice arrastáveis/
  redimensionáveis (handles nos cantos), snap a bordas e a outros slices (8px),
  zoom com scroll.
- **Painel Output**: wireframe dos monitores detectados (posição/resolução reais)
  com os `OutputRect` arrastáveis da mesma forma.
- Selecionar slice em qualquer painel destaca nos dois + campos numéricos
  (x, y, w, h, rotação, flips) para ajuste fino.
- Botões: adicionar/remover slice, adicionar tela (escolher monitor), duplicar
  slice, identify on/off.
- Edições aplicam **ao vivo** no engine (comando `UpdateOutputSetup`) — ver o
  telão reagir enquanto arrasta é essencial para alinhamento físico.

### T4.5 — Presets de output
- Serializar `OutputSetup` em `%AppData%\VideoWallRW\outputs\{nome}.json`
  (a serialização do Core já precisa existir minimamente aqui; a fase 7 completa
  o projeto inteiro).
- UI: combo de presets + salvar/salvar como/excluir. O projeto referencia o
  preset por nome, mas embute uma cópia (abrir projeto em outra máquina sem o
  preset ainda funciona).
- Migração: `bezel/gap` são só uma questão de onde os InputRects estão — sem
  feature extra; documentar exemplo no preset default ("2×1 monitores com bezel").

---

## Armadilhas conhecidas
- Coordenadas de monitor em **pixels físicos** (PerMonitorV2 da fase 0) — o
  editor converte para o espaço lógico do WPF só na visualização.
- Rotação 90/270 troca aspecto do quad — os testes de `SliceMath` cobrem isso
  antes de debugar em GPU.
- Miniatura ao vivo no editor: reusar a textura de preview da UI (downscale já
  existente), **não** criar segundo caminho de readback.
- Monitor do preset ausente na máquina (levou o projeto pra outro PC): tela fica
  `Enabled=false` com aviso, nunca crash.
- Slices com `InputRect` parcialmente fora da composição: clampar UV e
  preencher o excesso com preto (não esticar borda).

## DoD — verificação manual
1. Composição 3840×1080 fatiada em 2 monitores 1920×1080 lado a lado: conteúdo
   contínuo ao atravessar a fronteira (régua/grid pattern).
2. Compensação de bezel: afastar os InputRects 60px → objeto em movimento
   "atravessa" a moldura de forma convincente.
3. Um slice em rotação 90 (monitor em pé) com conteúdo correto.
4. Identify mostra nome/resolução em cada saída.
5. Correção de cor: baixar gamma de uma tela e ver diferença lado a lado.
6. Arrastar slice no editor reflete no telão em tempo real.
7. Salvar preset, limpar tudo, carregar preset → layout idêntico.
8. Testes de `SliceMath` passam (rotação, flip, clamp).
