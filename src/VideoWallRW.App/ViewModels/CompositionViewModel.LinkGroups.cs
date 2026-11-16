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
    // ── Grupos de troca (T20): células vinculadas trocam juntas ─────────────────

    private IEnumerable<ClipViewModel> AllClips() => Layers.SelectMany(l => l.Clips);

    /// <summary>Copia a célula alterada pras outras do mesmo grupo (mídia + todas as
    /// configurações) — chamado depois de qualquer edição/troca de mídia numa célula
    /// vinculada. O grupo é lido do ViewModel (autoritativo, existe até em célula
    /// vazia), então células vazias vinculadas TAMBÉM recebem a mídia (e a miniatura).</summary>
    private void PropagateLinkGroup(ClipViewModel source)
    {
        if (source.LinkGroupId is not { } groupId || source.Model is null)
            return;

        foreach (var other in AllClips())
        {
            if (ReferenceEquals(other, source) || other.LinkGroupId != groupId)
                continue;

            // Mantém a identidade da célula destino (CloneWithId): trocar o Id a cada
            // propagação fazia o autopilot perder a coluna atual e voltar pra coluna 0.
            var propagated = source.Model.CloneWithId(other.Model?.Id ?? Guid.NewGuid());
            propagated.LinkGroupId = groupId;
            Model.Layers[other.LayerIndex].Clips[other.ColumnIndex] = propagated;
            other.SetModel(propagated);
            _render.Post(new UpdateClip(other.LayerIndex, other.ColumnIndex, propagated));
        }
    }

    /// <summary>Vincula todas as células marcadas (Ctrl+clique) num grupo de troca novo —
    /// precisa de pelo menos 2 células, COM OU SEM mídia (célula vazia entra no grupo e
    /// recebe a mídia quando qualquer uma do grupo for preenchida). Botão direito >
    /// "Vincular células".</summary>
    public void LinkMultiSelectedCells()
    {
        var selected = AllClips().Where(c => c.IsMultiSelected).ToList();
        if (selected.Count < 2)
            return;

        SnapshotForUndo();
        var groupId = Guid.NewGuid();
        foreach (var clip in selected)
        {
            clip.LinkGroupId = groupId;
            clip.IsMultiSelected = false;
            if (clip.Model is not null)
            {
                clip.Model.LinkGroupId = groupId;
                _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
            }
        }
        // Se alguma já tinha mídia, propaga pra igualar o grupo na hora (inclusive
        // pras vazias); se todas estavam vazias, o grupo só espera a primeira mídia.
        var seed = selected.FirstOrDefault(c => c.Model is not null);
        if (seed is not null)
            PropagateLinkGroup(seed);
        Changed?.Invoke();
    }

    /// <summary>Tira uma célula do grupo de troca (as outras continuam vinculadas entre si).
    /// Funciona em célula vazia também (o vínculo é do ViewModel).</summary>
    public void UnlinkCell(ClipViewModel clip)
    {
        if (clip.LinkGroupId is null)
            return;

        SnapshotForUndo();
        clip.LinkGroupId = null;
        if (clip.Model is not null)
        {
            clip.Model.LinkGroupId = null;
            _render.Post(new UpdateClip(clip.LayerIndex, clip.ColumnIndex, clip.Model));
        }
        Changed?.Invoke();
    }

    public void ClearMultiSelection()
    {
        foreach (var clip in AllClips())
            clip.IsMultiSelected = false;
    }

    /// <summary>Vínculos de células VAZIAS pro arquivo do projeto — as com mídia já
    /// guardam o grupo no próprio Clip (ver ProjectMeta.BlankCellLinks).</summary>
    public Dictionary<string, Guid> ExportBlankCellLinks()
    {
        CaptureBlankCellLinks();
        var map = new Dictionary<string, Guid>();
        foreach (var ((layer, column), group) in _blankCellLinks)
            map[$"{layer}:{column}"] = group;
        return map;
    }

    /// <summary>Restaura os vínculos de células vazias ao abrir o projeto.</summary>
    public void ImportBlankCellLinks(Dictionary<string, Guid> map)
    {
        // Zera o vínculo das células vazias ANTES de aplicar o novo mapa: sem isto,
        // trocar de cena deixava as vazias com o grupo da cena anterior (Restore só
        // ESCREVE o que está no mapa, nunca apaga o que não está).
        foreach (var cell in AllClips())
            if (cell.Model is null)
                cell.LinkGroupId = null;

        _blankCellLinks.Clear();
        foreach (var (key, group) in map)
        {
            var parts = key.Split(':');
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], out int layerIndex) ||
                !int.TryParse(parts[1], out int columnIndex))
                continue;
            _blankCellLinks[(layerIndex, columnIndex)] = group;
        }
        // Aplica já nos ViewModels atuais (o projeto acabou de ser carregado) e mantém
        // no dicionário pra sobreviver às próximas reconstruções da grade.
        RestoreBlankCellLinks();
    }

    private LayerViewModel CreateLayerViewModel(Layer layer, int layerIndex)
    {
        var vm = new LayerViewModel(layer, layerIndex, _render, SelectClip, OnClipChanged);
        vm.SyncComposition = SyncToEngine;   // ver LayerViewModel.IgnoreColumnTrigger
        vm.Edited = () => Edited?.Invoke();  // ajuste de layer também suja o projeto
        return vm;
    }

}
