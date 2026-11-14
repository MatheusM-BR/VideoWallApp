using VideoWallRW.Core.Model;

namespace VideoWallRW.Core.Persistence;

public sealed class ProjectMeta
{
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
    public string? OutputPresetName { get; set; }

    /// <summary>Autopilot geral (toggle global da barra superior) — sem isso, reabrir
    /// um projeto sempre voltava com autopilot ligado, perdendo o estado real salvo.</summary>
    public bool AutopilotAllEnabled { get; set; } = true;

    /// <summary>Sequenciador de colunas (modo Instalação) rodando ou não — sem isso,
    /// reabrir sempre parava o sequenciador mesmo que estivesse ativo ao salvar.</summary>
    public bool SequencerRunning { get; set; }

    /// <summary>Monitores com a saída LIGADA (checkbox da linha SAÍDAS) no momento do
    /// save — reabrir o projeto religa os telões sozinho, sem o operador ter que
    /// marcar cada monitor de novo (T32: "mantenha até a habilitação dos vídeos").</summary>
    public List<string> EnabledOutputs { get; set; } = [];

    /// <summary>
    /// Vínculo (grupo de troca) de células que estão VAZIAS, na chave "layer:coluna".
    /// Célula com mídia guarda o grupo no próprio Clip; a vazia não tem Clip onde
    /// guardar, então ia embora ao salvar — por isso vive aqui.
    /// </summary>
    public Dictionary<string, Guid> BlankCellLinks { get; set; } = [];

    /// <summary>
    /// Cena que estava ABERTA no momento do save. Sem isto, reabrir o projeto não
    /// destacava aba nenhuma: a grade voltava certa (vem de Composition, que é a
    /// grade da cena aberta), mas nenhuma cena aparecia selecionada, e o operador
    /// tinha que clicar na aba de novo pra "voltar" pra onde já estava. Null = o
    /// projeto foi salvo sem cena aberta.
    /// </summary>
    public Guid? SelectedSceneId { get; set; }
}

/// <summary>
/// Cena = uma grade completa e independente (layers, colunas, clipes com todas as
/// configurações, master fader, blackout) — funciona como uma aba: abrir uma cena
/// nova começa em branco, clicar numa cena existente troca a grade inteira pra a
/// dela, e editar a grade com uma cena selecionada atualiza essa cena sozinho
/// (ver MainViewModel.SyncSelectedSceneFromGrid). Campos antigos (ActiveColumns/
/// LayerOpacities/MasterFader, versão "disparar preset" pré-T30) foram removidos —
/// projetos salvos por essa versão anterior carregam com Grid vazio (Composition
/// default), sem migração automática de dados antigos.
/// </summary>
public sealed class SceneSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Cena";
    public string? Hotkey { get; set; }

    public Composition Grid { get; set; } = new();

    /// <summary>
    /// Vínculo (grupo de troca) das células VAZIAS desta cena, em "layer:coluna".
    /// Célula com mídia guarda o grupo no próprio Clip, que já viaja dentro de
    /// <see cref="Grid"/>; a vazia não tem Clip onde guardar. Antes existia só o
    /// mapa global em <see cref="ProjectMeta.BlankCellLinks"/> — e como ele não
    /// pertencia a cena nenhuma, trocar de cena levava junto os vínculos vazios da
    /// cena anterior. Campo novo é opcional: projeto salvo antes dele carrega com o
    /// dicionário vazio e cai no mapa global, sem migração.
    /// </summary>
    public Dictionary<string, Guid> BlankCellLinks { get; set; } = [];
}

/// <summary>
/// Conteúdo completo de um projeto .vwrw — composição, output, cenas e metadados.
/// <see cref="SchemaVersion"/> permite migração de projetos antigos (ver
/// <see cref="ProjectSerializer"/>); um SchemaVersion MAIOR que o suportado pelo
/// app atual é recusado com mensagem clara, nunca carregado parcialmente.
/// </summary>
public sealed class ProjectFile
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public Composition Composition { get; set; } = new();
    public OutputSetup Output { get; set; } = new();
    public List<SceneSnapshot> Snapshots { get; set; } = [];
    public ProjectMeta Meta { get; set; } = new();
    public InstallationConfig Installation { get; set; } = new();
}
