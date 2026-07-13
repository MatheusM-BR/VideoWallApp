# VideoWallRW — Guia de Desenvolvimento (como usar os walkthroughs)

Este guia organiza a implementação em **10 fases** (`docs/fases/FASE-0.md` … `FASE-9.md`).
Cada fase é uma ordem de serviço autocontida para ser entregue a uma IA (ou dev)
implementar, com tarefas numeradas (`T0.1`, `T0.2`…), contratos de código,
armadilhas conhecidas e critério de conclusão (DoD) verificável.

## Como passar uma fase para a IA

Prompt modelo (uma fase por sessão de trabalho):

```
Leia ARQUITETURA.md e docs/fases/FASE-N.md deste projeto.
Implemente a fase N seguindo as tarefas na ordem (T N.1, T N.2, ...).
Regras:
- Siga as convenções de docs/DESENVOLVIMENTO.md.
- Um commit por tarefa concluída, mensagem "T N.x: descrição".
- Ao final, execute a verificação do DoD e reporte o resultado real
  (incluindo o que falhou, se falhar).
- Não avance para a fase seguinte.
```

Regras de progressão:

1. **Nunca iniciar uma fase com o DoD da anterior quebrado.**
2. Se durante uma fase surgir algo da fase seguinte ("já que estou aqui..."),
   **não fazer** — anotar em `docs/BACKLOG.md`.
3. Se uma decisão do walkthrough se mostrar errada na prática, a IA deve parar,
   explicar o conflito e propor alternativa — não improvisar em silêncio.
4. Ao concluir a fase, atualizar a tabela de status abaixo.

## Status

| Fase | Descrição | Status |
|------|-----------|--------|
| 0 | Fundação D3D11 multi-monitor | pendente |
| 1 | Player de vídeo (FFmpeg SW) | pendente |
| 2 | Grade layers × colunas | pendente |
| 3 | Transporte, atalhos, autopilot | pendente |
| 4 | Advanced Output (slices) | pendente |
| 5 | Fontes ao vivo (NDI + captura) | pendente |
| 6 | Performance (HW decode, HAP, preroll) | pendente |
| 7 | Persistência e operação | pendente |
| 8 | Modo instalação fixa | pendente |
| 9 | Extras | pendente |
| 10 | Módulo Agenda: banner de eventos + editor de imagem (paralelizável após a Fase 3) | pendente |

## Convenções globais (valem para todas as fases)

### Código
- **Identificadores em inglês** (classes, métodos, variáveis). Strings de UI e
  comentários em pt-BR.
- C# 12, `nullable enable`, `TreatWarningsAsErrors` em todos os projetos.
- MVVM na UI com CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`).
- Todo recurso não gerenciado (texturas, contexts FFmpeg, handles) embrulhado em
  classe `IDisposable` própria. Nada de ponteiro FFmpeg/D3D vazando para fora do
  wrapper.

### Threading (regra de ouro)
- **Só a render thread toca o `ImmediateContext` do D3D11.** Decoders e capturas
  produzem dados em buffers próprios; o upload para textura acontece na render
  thread (exceto texturas hw-decode da fase 6, que já nascem na GPU e são copiadas
  via `CopySubresourceRegion` na render thread).
- UI → engine **somente** via `Channel<EngineCommand>`. UI nunca chama método do
  engine diretamente.
- Engine → UI **somente** via snapshots imutáveis (evento `EngineStateChanged`
  com struct/record copiado) marshalado pro dispatcher.
- Nenhum `lock` compartilhado entre render thread e decoders além das filas.

### Erros e logs
- Serilog em todos os projetos (arquivo rotativo em `%LocalAppData%\VideoWallRW\logs`
  + sink de debug). Proibido `Console.WriteLine`.
- Falha de fonte (arquivo corrompido, NDI caiu, placa desconectada) **nunca**
  derruba o engine: o clip entra em estado `Error` com mensagem, e o render segue.
- Exceção não tratada na render thread: logar, tentar recriar device uma vez
  (device lost), senão sinalizar a UI e encerrar saídas com blackout.

### Build e verificação
- `dotnet build -warnaserror` limpo ao final de cada tarefa.
- Camada de debug do D3D11 ativa em DEBUG; ao sair, `ReportLiveDeviceObjects`
  não pode acusar vazamento (exceto os objetos do próprio relatório).
- Testes unitários só no `Core` (modelo, serialização, matemática de resize/slice)
  — não tentar testar GPU/FFmpeg em unit test; isso é verificação manual do DoD.

### Estrutura da solução (referências permitidas)

```
VideoWallRW.Core      → (nada; só BCL)          modelo, comandos, projeto/JSON
VideoWallRW.Engine    → Core, Vortice.*          device, render loop, saídas, composição
VideoWallRW.Media     → Core, Engine, FFmpeg     decoders de arquivo, thumbnails, áudio
VideoWallRW.Sources   → Core, Engine             NDI, Media Foundation, (desktop/Spout depois)
VideoWallRW.App       → todos                    WPF
VideoWallRW.Watchdog  → (nada)                   supervisor (fase 8)
tests/VideoWallRW.Core.Tests → Core
```

Dependência proibida: Engine/Media/Sources **nunca** referenciam WPF.

### Interface central entre fontes e engine

Toda fonte de pixels (arquivo, NDI, captura...) implementa:

```csharp
public interface IFrameSource : IDisposable
{
    SourceState State { get; }                 // Arming | Ready | Playing | Error
    string? ErrorMessage { get; }
    // Chamado pela render thread a cada frame. Retorna a SRV atual (pode repetir
    // a anterior se não chegou frame novo). Null = ainda sem frame (não desenhar).
    ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds);
    int Width { get; }
    int Height { get; }
    void Start(); void Stop();
}
```

Fontes com transporte (arquivo de vídeo/imagem) implementam também:

```csharp
public interface IPlayableSource : IFrameSource
{
    double Duration { get; }
    bool SupportsSeek { get; }
    void Seek(double seconds);
    event Action? EndReached;   // fim natural (para autopilot / play modes)
}
```
