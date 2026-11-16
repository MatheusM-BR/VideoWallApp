using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoWallRW.Core.Commands;
using VideoWallRW.Core.Stats;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using VideoWallRW.Engine.Output;
using VideoWallRW.App.ViewModels;
using VideoWallRW.Core.Model;
using VideoWallRW.Core.Persistence;

namespace VideoWallRW.App;

public sealed partial class MainViewModel
{
    // ── Cenas (T30): cada cena é uma grade completa e independente, como uma aba ──

    [RelayCommand]
    private void NewScene()
    {
        // A composição inicial podia estar fora de qualquer cena. Antes de abrir a
        // nova grade, registra o estado atual para que ele continue acessível.
        EnsureCurrentGridHasScene();
        SyncSelectedSceneFromGrid();

        var blank = new Composition { ColumnCount = 8 };
        blank.AddLayer(new Layer { Name = "Camada 3" });
        blank.AddLayer(new Layer { Name = "Camada 2" });
        blank.AddLayer(new Layer { Name = "Camada 1" });

        var snapshot = new SceneSnapshot { Name = $"Cena {Scenes.Count + 1}", Grid = blank };
        var vm = NewSceneViewModel(snapshot);
        Scenes.Add(vm);
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        SelectScene(vm); // "abrir uma cena nova é um cenário limpo" — já entra nela
        MarkDirty();
    }

    /// <summary>Garante que a grade visível pertença a uma cena. Projetos novos e
    /// arquivos antigos sem SelectedSceneId deixam de ter uma composição “solta”
    /// que desaparece quando o operador cria a próxima cena.</summary>
    private void EnsureCurrentGridHasScene()
    {
        if (SelectedScene is not null) return;

        var grid = Grid.Model.Clone();
        var snapshot = new SceneSnapshot
        {
            Name = $"Cena {Scenes.Count + 1}",
            Grid = grid,
            BlankCellLinks = Grid.ExportBlankCellLinks(),
        };
        var scene = NewSceneViewModel(snapshot);
        Scenes.Add(scene);
        SelectedScene = scene;
        scene.IsSelected = true;
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
    }

    /// <summary>Troca a grade inteira da tela do operador pra a da cena clicada — como
    /// trocar de aba. A cena que sai é congelada aqui mesmo, na primeira linha: o sync
    /// contínuo do Grid.Changed só enxerga mudança ESTRUTURAL, então ajuste de
    /// propriedade (volume, duração, resize, transform) só entra por este caminho.</summary>
    private void SelectScene(SceneViewModel scene)
    {
        if (ReferenceEquals(SelectedScene, scene)) return;

        // Congela a cena que está SAINDO com o estado atual da grade. Edições de
        // propriedade de clip/layer não disparam Changed, então o sync contínuo não
        // as tinha visto: sem esta linha, ajustar volume/duração/resize e clicar em
        // outra aba fazia a cena voltar com a versão velha, perdendo o ajuste.
        SyncSelectedSceneFromGrid();

        if (SelectedScene is not null)
            SelectedScene.IsSelected = false;
        SelectedScene = scene;
        scene.IsSelected = true;

        // Lê os valores da cena ANTES de mexer em qualquer coisa: atribuir MasterFader/
        // Blackout dispara SyncSelectedSceneFromGrid, que sobrescreve scene.Model.Grid
        // com um clone carimbado com os valores AINDA da cena anterior. Sem guardar
        // antes (e sem suprimir o sync durante a troca), o Blackout salvo era perdido
        // e a cena abria com o estado errado.
        double masterOfScene = scene.Model.Grid.MasterOpacity;
        bool blackoutOfScene = scene.Model.Grid.Blackout;

        _switchingScene = true;
        try
        {
            Grid.ReplaceModel(scene.Model.Grid.Clone());
            Grid.ImportBlankCellLinks(scene.Model.BlankCellLinks); // vínculos vazios são da cena
            MasterFader = masterOfScene;
            Blackout = blackoutOfScene;
        }
        finally
        {
            _switchingScene = false;
        }

        RefreshMonitorRouteOptions();
        MarkDirty();
    }

    /// <summary>Suprime o auto-save da cena enquanto a troca está em andamento (a grade
    /// e o master/blackout mudam em passos, e cada passo dispararia um snapshot parcial).</summary>
    private bool _switchingScene;

    /// <summary>Atalhos PageDown/PageUp (KeymapService) — percorre as cenas na ordem em
    /// que aparecem na barra, dando a volta nas pontas. Sem cena nenhuma criada ainda,
    /// não faz nada (não existe "cena 0" implícita).</summary>
    public void SelectNextScene() => SelectSceneByOffset(1);
    public void SelectPreviousScene() => SelectSceneByOffset(-1);

    private void SelectSceneByOffset(int offset)
    {
        if (Scenes.Count == 0) return;
        int current = SelectedScene is null ? -1 : Scenes.IndexOf(SelectedScene);
        int next = ((current + offset) % Scenes.Count + Scenes.Count) % Scenes.Count;
        SelectScene(Scenes[next]);
    }

    /// <summary>Chamado a cada Grid.Changed (qualquer edição de célula/coluna/layer) —
    /// clona a grade inteira de volta pra dentro da cena aberta agora, pra "editar com
    /// uma cena selecionada já atualiza ela sozinha" funcionar sem passo manual.</summary>
    private void SyncSelectedSceneFromGrid()
    {
        if (SelectedScene is null || _switchingScene) return;
        var snapshot = Grid.Model.Clone();
        snapshot.MasterOpacity = MasterFader;
        snapshot.Blackout = Blackout;
        SelectedScene.Model.Grid = snapshot;
        SelectedScene.Model.BlankCellLinks = Grid.ExportBlankCellLinks();
    }

    /// <summary>Ponto único de criação da aba de cena — garante que renomear/atribuir
    /// atalho também suje o projeto (ver SceneViewModel.Edited).</summary>
    private SceneViewModel NewSceneViewModel(SceneSnapshot snapshot)
        => new(snapshot, SelectScene, RemoveSceneInternal) { Edited = MarkDirty };

    private void RemoveSceneInternal(SceneViewModel scene)
    {
        var confirm = MessageBox.Show($"Excluir a cena \"{scene.Name}\"?", "RWVideoWall",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        if (ReferenceEquals(SelectedScene, scene))
            SelectedScene = null;
        Scenes.Remove(scene);
        _render.Post(new SyncScenes(Scenes.Select(s => s.Model).ToList()));
        MarkDirty();
    }

}
