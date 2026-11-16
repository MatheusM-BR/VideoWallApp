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
    // ── Fase 3/7: transporte e master fader/blackout ────────────────────────

    partial void OnMasterFaderChanged(double value)
    {
        _render.Post(new SetMasterOpacity(value));
        SyncSelectedSceneFromGrid();
        MarkDirty(); // master fader vai pro arquivo (Composition.MasterOpacity)
    }

    partial void OnBlackoutChanged(bool value)
    {
        _render.Post(new SetBlackout(value));
        SyncSelectedSceneFromGrid();
        MarkDirty(); // idem blackout (Composition.Blackout)
    }

    private void OnStatsUpdated(EngineStats stats)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            foreach (var monitor in Monitors)
            {
                var output = stats.Outputs.FirstOrDefault(
                    o => o.MonitorDeviceName == monitor.Info.DeviceName);
                monitor.FpsText = output is null ? "" : $"{output.Fps:F1} fps";
            }

            string outputsText = stats.Outputs.Count > 0
                ? string.Join("   |   ", stats.Outputs.Select(o => $"{o.MonitorDeviceName}: {o.Fps:F1} fps"))
                : "Nenhuma saída ativa";
            string dropsText = stats.DroppedFrames > 0 ? $"   |   ⚠ {stats.DroppedFrames} drops" : "";
            StatusText = $"{outputsText}   |   {stats.ActiveClipCount} clip(s) ativo(s){dropsText}";

            if (stats.Positions is not null)
                Grid.UpdatePlayheads(stats.Positions);

            RefreshActiveClipThumbnail();
        });
    }

    /// <summary>
    /// Chamado (na UI) depois que o engine morreu e o estado já foi salvo. O App decide
    /// como reiniciar: pelo watchdog, se ele estiver presente, ou relançando o processo.
    /// </summary>
    public Action<string>? EngineRecoveryRequested { get; set; }

    private void OnEngineFailed(string message)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            // Salva ANTES de desligar os monitores: o autosave grava quais telões
            // estavam ligados (EnabledOutputs), e é isso que o processo reiniciado religa.
            Autosave();
            SaveRuntimeState();
            StatusText = $"ERRO no engine: {message} — reiniciando…";
            foreach (var monitor in Monitors) monitor.IsActive = false;
            EngineRecoveryRequested?.Invoke(message);
        });
    }

    /// <summary>
    /// Retoma a última sessão depois de um reinício sem operador (modo kiosk/watchdog ou
    /// recuperação de falha do engine): prefere o autosave quando ele é mais novo que o
    /// arquivo do projeto (edições ainda não salvas), e religa exatamente o que estava no ar.
    /// </summary>
    public bool ResumeLastSession()
    {
        var runtime = RuntimeStateService.TryLoad();
        if (runtime is null) return false;

        string? projectPath = runtime.ProjectPath is { } p && File.Exists(p) ? p : null;
        string autosavePath = AutosaveService.GetAutosavePath(projectPath);
        bool autosaveIsNewer = File.Exists(autosavePath) &&
            (projectPath is null || File.GetLastWriteTimeUtc(autosavePath) > File.GetLastWriteTimeUtc(projectPath));

        if (autosaveIsNewer)
        {
            ApplyProject(ProjectSerializer.Load(autosavePath));
            _currentProjectPath = projectPath;
            _isDirty = true; // conteúdo veio do autosave: ainda não está no arquivo do usuário
            UpdateWindowTitle();
        }
        else if (projectPath is not null)
        {
            LoadProjectSilently(projectPath);
        }
        else
        {
            return false;
        }

        ApplyRuntimeState(runtime);
        return true;
    }

    /// <summary>Mantém o nome antigo por compatibilidade com o keymap ("ToggleMasterPause"
    /// e a tecla Espaço) — hoje alterna o autopilot global.</summary>
    public void ToggleMasterPause() => AutopilotAll = !AutopilotAll;

    /// <summary>Ao restaurar um projeto salvo, o toggle global não deve forçar todas as
    /// layers a seguir o mesmo valor — cada uma já veio com o AutopilotEnabled certo
    /// pelo próprio projeto (podem ter sido salvas em estados diferentes entre si).</summary>
    private bool _restoringAutopilotAll;

    partial void OnAutopilotAllChanged(bool value)
    {
        if (_restoringAutopilotAll) return;

        // "Envia comando para todos": o toggle global dirige o toggle de cada layer.
        foreach (var layer in Grid.Layers)
            layer.AutopilotEnabled = value;
        StatusText = value
            ? "Troca automática ligada em todas as camadas"
            : "Troca automática desligada — os vídeos continuam; ao terminar, o clipe permanece na camada";
        MarkDirty(); // vai pro arquivo (ProjectMeta.AutopilotAllEnabled)
    }

    public void ToggleBlackout() => Blackout = !Blackout;

    public void ToggleOutputs()
    {
        bool enableOutputs = Monitors.All(monitor => !monitor.IsActive);
        foreach (var monitor in Monitors)
            monitor.IsActive = enableOutputs;
    }
}
