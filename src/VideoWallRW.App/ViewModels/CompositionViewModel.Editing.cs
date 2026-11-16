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

public sealed partial class CompositionViewModel
{
    // ── Desfazer (grade): snapshot da composição antes de cada operação destrutiva ──
    private readonly Stack<Composition> _gridUndoStack = new();
    private readonly Stack<Composition> _gridRedoStack = new();
    private const int MaxGridUndo = 10;

    public bool CanUndoGrid => _gridUndoStack.Count > 0;
    public bool CanRedoGrid => _gridRedoStack.Count > 0;

    private void SnapshotForUndo()
    {
        _gridUndoStack.Push(Model.Clone());
        _gridRedoStack.Clear();
        while (_gridUndoStack.Count > MaxGridUndo)
        {
            var arr = _gridUndoStack.ToArray();
            _gridUndoStack.Clear();
            for (int i = arr.Length - 2; i >= 0; i--) _gridUndoStack.Push(arr[i]);
        }
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
    }

    /// <summary>Restaura a grade ao estado anterior à última ação (limpar/remover/
    /// clonar coluna, duplicar layer, definir/limpar célula...).</summary>
    [RelayCommand]
    private void UndoGrid()
    {
        if (_gridUndoStack.Count == 0) return;
        _gridRedoStack.Push(Model.Clone());
        ReplaceModel(_gridUndoStack.Pop());
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
        Changed?.Invoke();
    }

    [RelayCommand]
    private void RedoGrid()
    {
        if (_gridRedoStack.Count == 0) return;
        _gridUndoStack.Push(Model.Clone());
        ReplaceModel(_gridRedoStack.Pop());
        OnPropertyChanged(nameof(CanUndoGrid));
        OnPropertyChanged(nameof(CanRedoGrid));
        Changed?.Invoke();
    }

    // ── Operações de coluna (menu de contexto no cabeçalho) ─────────────────────

    /// <summary>Limpa o conteúdo (imagens/vídeos) de todas as células da coluna.</summary>
    public void ClearColumn(int col)
    {
        if (col < 0 || col >= Model.ColumnCount) return;
        SnapshotForUndo();
        for (int l = 0; l < Model.Layers.Count; l++)
            ClearCellInternal(l, col);
        Changed?.Invoke();
    }

    public void RemoveColumn(int col)
    {
        if (col < 0 || col >= Model.ColumnCount || Model.ColumnCount <= 1) return;
        SnapshotForUndo();
        // Ordem importa: o setter de Model.ColumnCount redimensiona (Array.Resize) o
        // array Clips de TODAS as layers pro novo tamanho — se isso rodasse antes do
        // RemoveAt, o array já teria encolhido cortando o ÚLTIMO slot (não o "col"
        // pedido) e, ao remover justo a última coluna, o RemoveAt(col) apontaria pra
        // um índice que não existe mais (era o bug real por trás de "deletar a última
        // coluna trava o app"). Por isso o RemoveAt manual roda primeiro, com o array
        // ainda no tamanho antigo, e só then o ColumnCount-- (que vira um no-op de
        // resize, já que os arrays já estão no tamanho certo).
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            // O array de uma layer pode estar DESSINCRONIZADO do ColumnCount (projeto
            // antigo, layer criada por caminho que não normalizou) — o guard lá em cima
            // valida contra ColumnCount, não contra este array. Sem este if, remover a
            // última coluna com um array mais curto estourava IndexOutOfRange e, fora
            // da UI thread do handler global, derrubava o processo inteiro (crash real
            // capturado no Event Log em 16/07 10:05).
            if (col < clips.Count)
                clips.RemoveAt(col);
            while (clips.Count > Model.ColumnCount - 1)
                clips.RemoveAt(clips.Count - 1);
            while (clips.Count < Model.ColumnCount - 1)
                clips.Add(null);
            layer.Clips = [.. clips];
        }
        CaptureBlankCellLinks();
        ShiftBlankCellLinks(col, -1);   // vínculos das colunas seguintes andam pra trás
        _blankLinksPrepared = true;
        Model.ColumnCount--;
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    public void CloneColumn(int col) => InsertColumnInternal(col + 1, cloneFrom: col);

    public void InsertColumnBefore(int col) => InsertColumnInternal(col, cloneFrom: null);

    public void InsertColumnAfter(int col) => InsertColumnInternal(col + 1, cloneFrom: null);

    private void InsertColumnInternal(int index, int? cloneFrom)
    {
        index = System.Math.Clamp(index, 0, Model.ColumnCount);
        SnapshotForUndo();
        // Mesma questão de ordem do RemoveColumn: o Insert manual roda com o array
        // ainda no tamanho antigo (senão o ColumnCount++ já apenderia um slot extra no
        // fim antes do Insert, deixando o array 2 posições maior que ColumnCount).
        foreach (var layer in Model.Layers)
        {
            var clips = layer.Clips.ToList();
            // Mesma defesa do RemoveColumn: o array desta layer pode estar mais curto
            // que ColumnCount — normaliza antes pro Insert/índice nunca estourar.
            while (clips.Count < Model.ColumnCount)
                clips.Add(null);
            var cloned = cloneFrom is { } src && src < clips.Count ? clips[src]?.Clone() : null;
            clips.Insert(System.Math.Min(index, clips.Count), cloned);
            layer.Clips = [.. clips];
        }
        CaptureBlankCellLinks();
        ShiftBlankCellLinks(index, +1);  // abre espaço: vínculos daqui pra frente avançam
        _blankLinksPrepared = true;
        Model.ColumnCount++;
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    // ── Layer: duplicar ─────────────────────────────────────────────────────────

    public void DuplicateLayer(int layerIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count) return;
        SnapshotForUndo();
        var clone = Model.Layers[layerIndex].Clone();
        clone.Name = $"{Model.Layers[layerIndex].Name} (cópia)";
        Model.Layers.Insert(layerIndex + 1, clone);
        ReplaceModel(Model);
        Changed?.Invoke();
    }

    // ── Célula: limpar conteúdo ─────────────────────────────────────────────────

    public void ClearCell(int layerIndex, int columnIndex)
    {
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count ||
            columnIndex < 0 || columnIndex >= Model.ColumnCount) return;
        SnapshotForUndo();
        ClearCellInternal(layerIndex, columnIndex);
        Changed?.Invoke();
    }

    private void ClearCellInternal(int layerIndex, int columnIndex)
    {
        // Se a célula limpa era a que estava no ar, tira a layer do ar também.
        bool wasActive = Layers[layerIndex].LastTriggeredColumn == columnIndex;

        Model.Layers[layerIndex].Clips[columnIndex] = null;
        Layers[layerIndex].Clips[columnIndex].ClearModel();
        _render.Post(new UpdateClip(layerIndex, columnIndex, null));
        if (wasActive)
        {
            _render.Post(new ClearLayer(layerIndex));
            Layers[layerIndex].LastTriggeredColumn = -1;
        }
    }

    // ── Copy/paste de clip entre células (Ctrl+C / Ctrl+V com uma célula selecionada) ──
    private Clip? _clipboardClip;

    public bool CopySelectedClip()
    {
        if (SelectedClip?.Model is not { } model)
            return false;
        _clipboardClip = model.Copy();
        return true;
    }

    public bool PasteClipboardClip()
    {
        if (_clipboardClip is null || SelectedClip is null)
            return false;
        // Clone() gera um Id novo — célula colada é um clip independente (retrigger
        // e autopilot não confundem as duas cópias).
        SetClip(SelectedClip.LayerIndex, SelectedClip.ColumnIndex, _clipboardClip.Clone());
        return true;
    }

    private void SelectClip(ClipViewModel clip)
    {
        if (SelectedClip is not null)
            SelectedClip.IsSelected = false;
        SelectedClip = clip;
        clip.IsSelected = true;
    }

    private void OnClipChanged(ClipViewModel clip)
    {
        if (clip.Model is null)
            return;

        // Guarda contra ClipViewModel "órfão": uma sonda assíncrona (duração/dimensões,
        // ProbeDurationIfNeeded/ProbeDimensionsIfNeeded) pode terminar DEPOIS que a
        // coluna/layer dela já foi removida (RemoveColumn/RemoveLayer encolhem os
        // arrays) — sem isso, o callback tentava escrever num índice que não existe
        // mais e derrubava o app inteiro (IndexOutOfRangeException na UI thread, fora
        // do try/catch da render thread).
        if (clip.LayerIndex < 0 || clip.LayerIndex >= Model.Layers.Count)
            return;
        var clips = Model.Layers[clip.LayerIndex].Clips;
        if (clip.ColumnIndex < 0 || clip.ColumnIndex >= clips.Length)
            return;

        clips[clip.ColumnIndex] = clip.Model;
        _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
        PropagateLinkGroup(clip);
        Edited?.Invoke();
    }

    private const int MaxRecentSources = 20;

    /// <summary>Últimos arquivos enviados pra grade, mais recente primeiro — aba Sources.</summary>
    public ObservableCollection<string> RecentSources { get; } = [];

    public void SetFiles(int layerIndex, int startColumn, IEnumerable<string> files)
    {
        int column = startColumn;
        foreach (var file in files)
        {
            if (column >= Model.ColumnCount)
                break;

            var extension = Path.GetExtension(file).ToLowerInvariant();
            bool isImage = extension is ".png" or ".jpg" or ".jpeg" or ".bmp";
            SourceDescriptor source = isImage
                ? new FileImageSource(file)
                : new FileVideoSource(file);
            SetClip(layerIndex, column++, new Clip(source)
            {
                Name = Path.GetFileName(file),
                // Padrão da operação: imagem fica 7s no ar antes do autopilot avançar.
                ImageDuration = isImage ? 7 : null,
                // Todo clip novo entra em Fit: ocupa o máximo da composição respeitando
                // a proporção original, sem cortar nem esticar (pedido do operador).
                Resize = ResizeMode.Fit,
            });

            // Aba Sources > Recentes: só imagens (geralmente recortadas no editor) —
            // vídeo é conteúdo pesado/específico de cada leilão, não faz sentido
            // reaproveitar como "fonte recente" do mesmo jeito que um banner/logo.
            if (isImage)
            {
                RecentSources.Remove(file);
                RecentSources.Insert(0, file);
                while (RecentSources.Count > MaxRecentSources)
                    RecentSources.RemoveAt(RecentSources.Count - 1);
            }
        }
    }

    /// <summary>
    /// Fase 8 T8.6: adiciona um arquivo (de uma pasta monitorada) na primeira coluna
    /// vazia da layer; se não houver, cria uma coluna nova. Usado pelo watch-folder —
    /// nunca sobrescreve um clip já configurado manualmente.
    /// </summary>
    public void AddFileAutoColumn(int layerIndex, string path)
    {
        if (layerIndex < 0 || layerIndex >= Layers.Count)
            return;

        int column = Array.FindIndex(Model.Layers[layerIndex].Clips, c => c is null);
        if (column < 0)
        {
            AddColumn();
            column = Model.ColumnCount - 1;
        }
        SetFiles(layerIndex, column, [path]);
    }

    [RelayCommand]
    private void AddLayer()
    {
        var layer = new Layer { Name = $"Camada {Model.Layers.Count + 1}" };
        Model.AddLayer(layer);
        Layers.Add(CreateLayerViewModel(layer, Layers.Count));
        SyncToEngine();
        Changed?.Invoke();
    }

    /// <summary>Remove a layer inteira (botão direito no painel da layer) — mantém pelo
    /// menos uma layer. Reconstrói os ViewModels pra renumerar os índices das demais.</summary>
    public void RemoveLayer(int layerIndex)
    {
        if (Model.Layers.Count <= 1) return;
        if (layerIndex < 0 || layerIndex >= Model.Layers.Count) return;

        SnapshotForUndo();
        Model.Layers.RemoveAt(layerIndex);
        if (SelectedClip is not null)
        {
            SelectedClip.IsSelected = false;
            SelectedClip = null;
        }
        Layers.Clear();
        for (int i = 0; i < Model.Layers.Count; i++)
            Layers.Add(CreateLayerViewModel(Model.Layers[i], i));
        SyncToEngine();
        Changed?.Invoke();
    }

    [RelayCommand]
    private void AddColumn()
    {
        // SetColumnCount (não o setter puro): aqui NÃO há cirurgia manual no array —
        // é o próprio modelo que precisa crescer os slots de todas as layers.
        Model.SetColumnCount(Model.ColumnCount + 1);
        Columns.Add(Model.ColumnCount - 1);
        foreach (var layer in Layers)
            layer.AddClip(new ClipViewModel(null, layer.LayerIndex, Model.ColumnCount - 1, _render,
                SelectClip, OnClipChanged, clip => layer.LastTriggeredColumn = clip.ColumnIndex));
        SyncToEngine();
        Changed?.Invoke();
    }
}
