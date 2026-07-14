using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using VideoWallRW.Core.Model;

namespace VideoWallRW.App.ViewModels;

/// <summary>Um passo do sequenciador na UI (Fase 8 T8.4).</summary>
public sealed partial class SequencerStepViewModel : ObservableObject
{
    private readonly Action _changed;

    [ObservableProperty] private int _column;
    [ObservableProperty] private double _seconds = 30.0;

    public SequencerStepViewModel(SequencerStep model, Action changed)
    {
        _changed = changed;
        _column = model.Column;
        _seconds = model.Seconds;
    }

    public SequencerStep ToModel() => new() { Column = Column, Seconds = Seconds };

    partial void OnColumnChanged(int value) => _changed();
    partial void OnSecondsChanged(double value) => _changed();
}

/// <summary>Uma regra do agendador na UI (Fase 8 T8.5).</summary>
public sealed partial class ScheduleRuleViewModel : ObservableObject
{
    private readonly Action _changed;
    public Guid Id { get; }

    [ObservableProperty] private string _name = "Regra";
    [ObservableProperty] private bool _enabled = true;
    [ObservableProperty] private int _hour;
    [ObservableProperty] private int _minute;
    [ObservableProperty] private ScheduleActionType _action;
    [ObservableProperty] private int _columnOrParam;
    [ObservableProperty] private bool _monday = true, _tuesday = true, _wednesday = true,
        _thursday = true, _friday = true, _saturday = true, _sunday = true;

    public IReadOnlyList<ScheduleActionType> Actions { get; } = Enum.GetValues<ScheduleActionType>();

    public ScheduleRuleViewModel(ScheduleRule model, Action changed)
    {
        _changed = changed;
        Id = model.Id;
        _name = model.Name;
        _enabled = model.Enabled;
        _hour = model.Time.Hour;
        _minute = model.Time.Minute;
        _action = model.Action;
        _columnOrParam = model.ColumnOrParam;
        var days = model.Days.ToHashSet();
        _monday = days.Contains(DayOfWeek.Monday);
        _tuesday = days.Contains(DayOfWeek.Tuesday);
        _wednesday = days.Contains(DayOfWeek.Wednesday);
        _thursday = days.Contains(DayOfWeek.Thursday);
        _friday = days.Contains(DayOfWeek.Friday);
        _saturday = days.Contains(DayOfWeek.Saturday);
        _sunday = days.Contains(DayOfWeek.Sunday);
    }

    public ScheduleRule ToModel()
    {
        var days = new List<DayOfWeek>();
        if (Monday) days.Add(DayOfWeek.Monday);
        if (Tuesday) days.Add(DayOfWeek.Tuesday);
        if (Wednesday) days.Add(DayOfWeek.Wednesday);
        if (Thursday) days.Add(DayOfWeek.Thursday);
        if (Friday) days.Add(DayOfWeek.Friday);
        if (Saturday) days.Add(DayOfWeek.Saturday);
        if (Sunday) days.Add(DayOfWeek.Sunday);
        return new ScheduleRule
        {
            Id = Id,
            Name = Name,
            Enabled = Enabled,
            Days = days.ToArray(),
            Time = new TimeOnly(Hour, Minute),
            Action = Action,
            ColumnOrParam = ColumnOrParam,
        };
    }

    partial void OnNameChanged(string value) => _changed();
    partial void OnEnabledChanged(bool value) => _changed();
    partial void OnHourChanged(int value) => _changed();
    partial void OnMinuteChanged(int value) => _changed();
    partial void OnActionChanged(ScheduleActionType value) => _changed();
    partial void OnColumnOrParamChanged(int value) => _changed();
    partial void OnMondayChanged(bool value) => _changed();
    partial void OnTuesdayChanged(bool value) => _changed();
    partial void OnWednesdayChanged(bool value) => _changed();
    partial void OnThursdayChanged(bool value) => _changed();
    partial void OnFridayChanged(bool value) => _changed();
    partial void OnSaturdayChanged(bool value) => _changed();
    partial void OnSundayChanged(bool value) => _changed();
}

/// <summary>Uma pasta monitorada na UI (Fase 8 T8.6).</summary>
public sealed partial class WatchFolderViewModel : ObservableObject
{
    private readonly Action _changed;
    public Guid Id { get; }

    [ObservableProperty] private int _layerIndex;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private bool _enabled = true;

    public WatchFolderViewModel(WatchFolderConfig model, Action changed)
    {
        _changed = changed;
        Id = model.Id;
        _layerIndex = model.LayerIndex;
        _path = model.Path;
        _enabled = model.Enabled;
    }

    public WatchFolderConfig ToModel() => new()
    {
        Id = Id,
        LayerIndex = LayerIndex,
        Path = Path,
        Enabled = Enabled,
    };

    partial void OnLayerIndexChanged(int value) => _changed();
    partial void OnPathChanged(string value) => _changed();
    partial void OnEnabledChanged(bool value) => _changed();
}

/// <summary>
/// Editor do modo instalação fixa (Fase 8): sequenciador, agenda, pastas monitoradas e
/// autostart. Toda edição reconstrói o <see cref="InstallationConfig"/> e chama
/// <paramref name="onChanged"/> (a MainViewModel envia pro engine e marca o projeto sujo).
/// </summary>
public sealed partial class InstallationViewModel : ObservableObject
{
    private readonly Action<InstallationConfig> _onChanged;
    private bool _suppressPush;

    [ObservableProperty] private bool _sequencerEnabled;
    [ObservableProperty] private bool _sequencerLoop = true;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private string _statusText = "";

    public ObservableCollection<SequencerStepViewModel> Steps { get; } = [];
    public ObservableCollection<ScheduleRuleViewModel> Rules { get; } = [];
    public ObservableCollection<WatchFolderViewModel> WatchFolders { get; } = [];

    public InstallationViewModel(InstallationConfig config, Action<InstallationConfig> onChanged)
    {
        _onChanged = onChanged;
        _suppressPush = true;

        _sequencerEnabled = config.Sequencer.Enabled;
        _sequencerLoop = config.Sequencer.Loop;
        foreach (var step in config.Sequencer.Steps)
            Steps.Add(new SequencerStepViewModel(step, Push));
        foreach (var rule in config.ScheduleRules)
            Rules.Add(new ScheduleRuleViewModel(rule, Push));
        foreach (var wf in config.WatchFolders)
            WatchFolders.Add(new WatchFolderViewModel(wf, Push));

        try { _autostartEnabled = AutostartService.IsEnabled(); } catch { /* ambiente sem registro (testes) */ }

        _suppressPush = false;
    }

    [RelayCommand]
    private void AddStep()
    {
        Steps.Add(new SequencerStepViewModel(new SequencerStep(), Push));
        Push();
    }

    [RelayCommand]
    private void RemoveStep(SequencerStepViewModel step)
    {
        Steps.Remove(step);
        Push();
    }

    [RelayCommand]
    private void AddRule()
    {
        Rules.Add(new ScheduleRuleViewModel(new ScheduleRule(), Push));
        Push();
    }

    [RelayCommand]
    private void RemoveRule(ScheduleRuleViewModel rule)
    {
        Rules.Remove(rule);
        Push();
    }

    [RelayCommand]
    private void AddWatchFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Pasta monitorada" };
        if (dlg.ShowDialog() != true) return;
        WatchFolders.Add(new WatchFolderViewModel(
            new WatchFolderConfig { Path = dlg.FolderName }, Push));
        Push();
    }

    [RelayCommand]
    private void RemoveWatchFolder(WatchFolderViewModel wf)
    {
        WatchFolders.Remove(wf);
        Push();
    }

    partial void OnSequencerEnabledChanged(bool value) => Push();
    partial void OnSequencerLoopChanged(bool value) => Push();

    partial void OnAutostartEnabledChanged(bool value)
    {
        try
        {
            AutostartService.SetEnabled(value);
            StatusText = value ? "Inicialização automática ativada." : "Inicialização automática desativada.";
        }
        catch (Exception ex)
        {
            StatusText = $"Falha ao configurar autostart: {ex.Message}";
        }
    }

    public InstallationConfig ToModel() => new()
    {
        Sequencer = new SequencerConfig
        {
            Enabled = SequencerEnabled,
            Loop = SequencerLoop,
            Steps = Steps.Select(s => s.ToModel()).ToList(),
        },
        ScheduleRules = Rules.Select(r => r.ToModel()).ToList(),
        WatchFolders = WatchFolders.Select(w => w.ToModel()).ToList(),
        AutostartEnabled = AutostartEnabled,
    };

    private void Push()
    {
        if (_suppressPush) return;
        _onChanged(ToModel());
    }
}
