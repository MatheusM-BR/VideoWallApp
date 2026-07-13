# Fase 7 — Persistência e operação: projeto, autosave, snapshots, keymap

## Objetivo
O programa vira uma ferramenta confiável de trabalho: salvar/abrir projeto com
tudo dentro, autosave com recuperação de crash, relink de mídia movida,
snapshots de cena, e o editor de atalhos. Critério absoluto: **fechar e reabrir
restaura exatamente o estado**.

## Pré-requisitos
- Fase 6 concluída (DoD verificado).

## Entregáveis
1. Formato de projeto `.vwrw` (JSON versionado) com save/load completo.
2. Autosave + `.bak` + recuperação pós-crash.
3. Relink de mídia (hash + busca em pasta).
4. Snapshots de cena (salvar/disparar estado da grade).
5. Master fader/blackout definitivos + editor de keymap.
6. Diálogo "novo/abrir/salvar/salvar como" e mudanças-não-salvas.

---

## Tarefas

### T7.1 — Serialização do projeto
`src/VideoWallRW.Core/Persistence/`:
- `ProjectFile { int SchemaVersion; Composition Composition; OutputSetup Output;
  List<SceneSnapshot> Snapshots; ProjectMeta Meta; }`
- System.Text.Json com `[JsonDerivedType]` no `SourceDescriptor`
  (discriminador `"$type"`: `"fileVideo"`, `"fileImage"`, `"ndi"`, `"capture"`,
  `"solidColor"`, `"testPattern"`).
- `SchemaVersion = 1`; leitor com pipeline de migração
  (`Migrations.Apply(JsonNode, fromVersion)`) preparado desde já — mesmo vazio.
- Caminhos de mídia gravados **duplos**: relativo à pasta do projeto (preferido
  na leitura) + absoluto (fallback) + `FileFingerprint { long Size; ulong
  XxHash64First64Kb; }` para relink.
- Testes: round-trip completo (projeto sintético com todos os tipos de source),
  leitura de JSON com campos desconhecidos (ignorar, não quebrar).

### T7.2 — Save/Load no App
- Novo/Abrir/Salvar/Salvar como/Recentes (jump list + menu).
- Load: parar grade atual com fade rápido, carregar, reconstruir engine
  (comando `LoadProject(ProjectFile)` atômico), rearmar clips que estavam no ar?
  **Não** — projeto abre com grade parada (estado "no ar" é runtime, não
  projeto; exceção: modo instalação, fase 8).
- Flag "dirty" (título com `*`), confirmação ao fechar com mudanças.

### T7.3 — Autosave e recuperação
- Timer 120s (e em eventos grandes: adicionar layer, mudar output): gravação
  **atômica** (escrever `.tmp` → `File.Replace` mantendo `.bak`).
- Autosave vai para `%LocalAppData%\VideoWallRW\autosave\{hash-do-caminho}.vwrw`
  (não suja a pasta do usuário).
- Marca de sessão limpa: arquivo-sentinela criado no boot, removido no exit
  normal. No startup, sentinela presente + autosave mais novo que o projeto →
  oferecer recuperação.

### T7.4 — Relink de mídia
- No load, mídia ausente → clip em estado `Missing` (célula com badge, resto
  funciona).
- Diálogo "Relink": lista de faltantes; ações: localizar arquivo (file picker,
  aplica a pasta escolhida aos demais faltantes automaticamente) e "buscar em
  pasta..." (varredura recursiva casando por nome; ambiguidade resolvida pelo
  fingerprint).

### T7.5 — Snapshots de cena
```csharp
public sealed class SceneSnapshot {
    public Guid Id; public string Name; public string Hotkey?;
    public Dictionary<Guid /*layerId*/, int /*column*/> ActiveClips;
    public Dictionary<Guid, double> LayerOpacities;
    public double MasterFader;
}
```
- "Salvar cena" captura o estado atual (engine snapshot); disparar cena = aplicar
  tudo de uma vez (triggers + opacidades, com os crossfades de cada layer).
- UI: faixa de botões de cena acima da grade (como os "column triggers" do
  Resolume, mas de estado completo); atalhos `Ctrl+1..9` para as 9 primeiras.

### T7.6 — Master fader e blackout definitivos
- Consolidar o que veio das fases 3/4: master fader (slider no topo, 0–100%,
  aplicado no passe de saída) e blackout (botão + `B`) com fade configurável
  (default 0.5s) — blackout NÃO zera o fader (estados independentes; blackout é
  um "cala tudo" reversível).
- Preview do operador continua mostrando a composição mesmo em blackout
  (rótulo "BLACKOUT" vermelho sobreposto no monitor de programa da UI).

### T7.7 — Editor de keymap
- Tela de configurações → aba Atalhos: grade ação × gesto, clicar e teclar o
  novo atalho, detecção de conflito (aviso inline), reset para default.
- Persistir no mesmo `keymap.json` da fase 3.

---

## Armadilhas conhecidas
- `File.Replace` falha entre volumes — autosave e projeto ficam sempre no mesmo
  volume (autosave em LocalAppData é cópia independente, não Replace do
  original).
- Snapshot dispara N triggers de uma vez → passa pelo mesmo limite de aberturas
  simultâneas da fase 2 (semáforo) — cenas grandes armam em onda, e só trocam
  quando prontas (nunca meio-cena com buracos pretos: aplicar a troca de cada
  layer quando o clip dela armar, crossfade cobre a diferença).
- GUIDs de layer precisam ser estáveis entre save/load (snapshots referenciam).
- JSON de versões futuras (usuário abre projeto novo em app velho):
  `SchemaVersion` maior → recusar com mensagem clara, nunca carregar metade.

## DoD — verificação manual
1. Montar projeto com 4 layers, fontes mistas (arquivo/HAP/NDI/captura), output
   com 3 slices, 2 cenas → salvar → fechar → reabrir: **tudo idêntico**
   (incluindo posições de slice e atalhos de cena).
2. Mover a pasta de mídia → abrir → relink por pasta resolve todos de uma vez.
3. Matar o processo no gerenciador de tarefas → reabrir → oferta de recuperação
   restaura o trabalho não salvo.
4. Disparar cena troca as 4 layers + opacidades com crossfade, sem frame preto.
5. Remapear um atalho no editor e usá-lo em seguida.
6. Round-trip: testes de serialização passam; abrir projeto salvo à mão com
   campo extra desconhecido não quebra.
