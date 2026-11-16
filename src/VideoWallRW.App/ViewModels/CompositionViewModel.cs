using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine;

namespace VideoWallRW.App.ViewModels;

public sealed partial class CompositionViewModel : ObservableObject
{
    private readonly RenderThread _render;
    
    public Composition Model { get; }
    public ObservableCollection<LayerViewModel> Layers { get; } = [];
    public ObservableCollection<int> Columns { get; } = [];
    public IReadOnlyList<BlendMode> BlendModes { get; } = Enum.GetValues<BlendMode>();
    public IReadOnlyList<PlayMode> PlayModes { get; } = Enum.GetValues<PlayMode>();
    public IReadOnlyList<AutopilotAction> AutopilotActions { get; } = Enum.GetValues<AutopilotAction>();
    public IReadOnlyList<ResizeMode> ResizeModes { get; } = Enum.GetValues<ResizeMode>();

    [ObservableProperty] private ClipViewModel? _selectedClip;

    /// <summary>Mudanças estruturais (clip definido, layer/coluna adicionada) — usado para marcar o projeto como não salvo (T7.2).</summary>
    public event Action? Changed;

    /// <summary>
    /// Edição de PROPRIEDADE de clip ou layer (volume, duração da imagem, resize,
    /// velocidade, in/out, posição, escala, opacidade, blend, layer fixa...). Não
    /// muda a estrutura da grade, então não vale reconstruir combos de roteamento
    /// nem atalhos de coluna — mas TEM que sujar o projeto: essas edições passavam
    /// batido, o título não ganhava o "*", fechar não perguntava nada e o ajuste ia
    /// embora em silêncio. Mantido separado de <see cref="Changed"/> justamente
    /// porque dispara em rajada enquanto o operador arrasta um slider.
    /// </summary>
    public event Action? Edited;

    public CompositionViewModel(Composition model, RenderThread render)
    {
        Model = model;
        _render = render;

        for (int i = 0; i < model.ColumnCount; i++) Columns.Add(i);

        for (int i = 0; i < model.Layers.Count; i++)
        {
            Layers.Add(CreateLayerViewModel(model.Layers[i], i));
        }
    }

    /// <summary>Reconstrói toda a árvore de ViewModels a partir de um novo Composition (abrir projeto — T7.2).</summary>
    /// <summary>
    /// Vínculo de células VAZIAS (grupo de troca), por "layer:coluna". Célula com mídia
    /// guarda o grupo dentro do próprio Clip e sobrevive sozinha; a vazia não tem onde
    /// guardar, e o ViewModel dela é DESTRUÍDO em toda reconstrução da grade
    /// (ReplaceModel: troca de cena, carregar projeto, desfazer, add/remover coluna ou
    /// layer). Por isso o vínculo das vazias vive aqui, fora dos ViewModels.
    /// </summary>
    private readonly Dictionary<(int Layer, int Column), Guid> _blankCellLinks = [];

    /// <summary>Lê os vínculos das células vazias dos ViewModels atuais pro dicionário —
    /// chamado antes de destruir os VMs.</summary>
    private void CaptureBlankCellLinks()
    {
        _blankCellLinks.Clear();
        foreach (var cell in AllClips())
            if (cell.Model is null && cell.LinkGroupId is { } group)
                _blankCellLinks[(cell.LayerIndex, cell.ColumnIndex)] = group;
    }

    /// <summary>Reaplica os vínculos nas células vazias dos ViewModels recém-criados.</summary>
    private void RestoreBlankCellLinks()
    {
        foreach (var ((layerIndex, columnIndex), group) in _blankCellLinks)
        {
            if (layerIndex < 0 || layerIndex >= Layers.Count) continue;
            var clips = Layers[layerIndex].Clips;
            if (columnIndex < 0 || columnIndex >= clips.Count) continue;
            if (clips[columnIndex].Model is null)
                clips[columnIndex].LinkGroupId = group;
        }
    }

    /// <summary>Desloca os vínculos de células vazias quando uma coluna é inserida ou
    /// removida — sem isso eles ficariam apontando pra coluna errada depois da cirurgia
    /// (os clipes com mídia já se movem sozinhos, porque o grupo vai dentro do Clip).</summary>
    private void ShiftBlankCellLinks(int column, int delta)
    {
        var shifted = new Dictionary<(int, int), Guid>();
        foreach (var ((layer, col), group) in _blankCellLinks)
        {
            if (delta < 0 && col == column) continue;      // coluna removida: vínculo some
            int newCol = col >= column ? col + delta : col;
            if (newCol >= 0) shifted[(layer, newCol)] = group;
        }
        _blankCellLinks.Clear();
        foreach (var kv in shifted) _blankCellLinks[kv.Key] = kv.Value;
    }

    public void ReplaceModel(Composition newModel)
    {
        // Captura antes de destruir os VMs; restaura depois de recriá-los (ver campo).
        // ATENÇÃO: quem já ajustou o dicionário (ShiftBlankCellLinks, ImportBlankCellLinks)
        // sinaliza via _blankLinksPrepared pra não ser sobrescrito por uma captura tardia.
        if (!_blankLinksPrepared) CaptureBlankCellLinks();
        _blankLinksPrepared = false;

        Layers.Clear();
        Columns.Clear();
        if (SelectedClip is not null)
            SelectedClip.IsSelected = false;
        SelectedClip = null;

        Model.Width = newModel.Width;
        Model.Height = newModel.Height;
        Model.DefaultImageDuration = newModel.DefaultImageDuration;
        Model.MasterOpacity = newModel.MasterOpacity;
        Model.Blackout = newModel.Blackout;
        Model.ColumnCount = newModel.ColumnCount;
        Model.Layers = newModel.Layers;
        // Rede de segurança: projeto salvo por versão antiga (ou caminho que deixou o
        // array fora de sincronia) fica com layers de tamanho diferente do ColumnCount —
        // era a origem do IndexOutOfRange ao remover coluna. Normaliza na entrada.
        Model.NormalizeLayers();

        for (int i = 0; i < Model.ColumnCount; i++) Columns.Add(i);
        for (int i = 0; i < Model.Layers.Count; i++) Layers.Add(CreateLayerViewModel(Model.Layers[i], i));

        RestoreBlankCellLinks();
        SyncToEngine();
    }

    /// <summary>Marca que o dicionário de vínculos vazios já foi preparado por quem
    /// chamou (shift de coluna / import do projeto) — ver ReplaceModel.</summary>
    private bool _blankLinksPrepared;

    /// <summary>Distribui as posições de reprodução vindas dos stats do engine para as
    /// células certas (agulha da TrimBar). Chamado na UI thread ~2x/s.</summary>
    public void UpdatePlayheads(IReadOnlyList<VideoWallRW.Core.Stats.LayerClipPosition> positions)
    {
        foreach (var layer in Layers)
            foreach (var clip in layer.Clips)
            {
                var match = positions.FirstOrDefault(p =>
                    p.LayerIndex == layer.LayerIndex && clip.Model?.Id == p.ClipId);
                clip.PlayheadSeconds = match?.Playhead ?? -1;
            }
    }

    /// <summary>Estado atual da grade para capturar uma cena (T7.5): coluna e opacidade ativas por layer.</summary>
    public (Dictionary<Guid, int> ActiveColumns, Dictionary<Guid, double> Opacities) CaptureSceneState()
    {
        var columns = new Dictionary<Guid, int>();
        var opacities = new Dictionary<Guid, double>();
        foreach (var layer in Layers)
        {
            columns[layer.Model.Id] = layer.LastTriggeredColumn;
            opacities[layer.Model.Id] = layer.Opacity;
        }
        return (columns, opacities);
    }

    [RelayCommand]
    private void TriggerColumn(int colIndex)
    {
        if (colIndex < 0 || colIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerColumn(colIndex));
        MarkColumnTriggered(colIndex);
    }

    public void SyncToEngine()
    {
        _render.Post(new SyncComposition(Model));
    }

    public void TriggerColumnFromShortcut(int columnIndex)
    {
        if (columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerColumn(columnIndex));
        MarkColumnTriggered(columnIndex);
    }

    public void TriggerClipFromShortcut(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        _render.Post(new TriggerClip(layerIndex, columnIndex));
        Layers[layerIndex].LastTriggeredColumn =
            Model.Layers[layerIndex].Clips[columnIndex]?.Source is null ? -1 : columnIndex;
    }

    public void MarkClipTriggeredFromEngine(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Layers.Count) return;
        Layers[layerIndex].LastTriggeredColumn = columnIndex;
    }

    /// <summary>
    /// Consistência: o engine, ao disparar uma coluna, toca o slot com mídia e limpa
    /// os vazios — o rastreio da UI (LastTriggeredColumn, usado por cenas e pelo
    /// destaque de célula ativa) precisa espelhar exatamente isso. Antes, disparos
    /// por coluna/atalho não atualizavam o rastreio e a cena capturada saía errada.
    /// </summary>
    private void MarkColumnTriggered(int colIndex)
    {
        foreach (var layer in Layers)
        {
            // Layer fixa não entra no disparo de coluna (ver Layer.IgnoreColumnTrigger),
            // então o destaque dela também não pode mudar — senão a grade mostraria
            // como "no ar" uma célula que o engine nem disparou.
            if (layer.Model.IgnoreColumnTrigger) continue;
            layer.LastTriggeredColumn =
                layer.Model.Clips[colIndex]?.Source is null ? -1 : colIndex;
        }
    }

    public void ClearSelectedLayer()
    {
        if (SelectedClip is not null)
            _render.Post(new ClearLayer(SelectedClip.LayerIndex));
    }

    public void ClearAll() => _render.Post(new ClearAll());

    public void SetClip(int layerIndex, int columnIndex, Clip clip)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount)
            throw new ArgumentOutOfRangeException();

        SnapshotForUndo(); // sobrescrever uma célula é desfazível
        // Importar mídia numa célula vinculada (T20/T33) não tira ela do grupo — herda
        // o vínculo do ViewModel da célula (que existe até quando ela estava VAZIA),
        // não do Model antigo (que era null numa célula em branco). É isso que faz
        // "importar numa célula já vinculada" propagar e mostrar a miniatura nas demais.
        var targetVm = Layers[layerIndex].Clips[columnIndex];
        clip.LinkGroupId = targetVm.LinkGroupId;
        Model.Layers[layerIndex].Clips[columnIndex] = clip;
        targetVm.SetModel(clip);
        SelectClip(targetVm);
        _render.Post(new UpdateClip(layerIndex, columnIndex, clip));
        PropagateLinkGroup(targetVm);
        Changed?.Invoke();
    }

}
