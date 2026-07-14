namespace VideoWallRW.Core.Model;

/// <summary>Um passo do sequenciador de colunas (Fase 8 T8.4).</summary>
public sealed class SequencerStep
{
    public int Column { get; set; }
    public double Seconds { get; set; } = 30.0;
}

/// <summary>Playlist de colunas — roda sem UI, sobrevive a reinício via projeto salvo.</summary>
public sealed class SequencerConfig
{
    public bool Enabled { get; set; }
    public bool Loop { get; set; } = true;
    public List<SequencerStep> Steps { get; set; } = [];

    public SequencerConfig Clone() => new()
    {
        Enabled = Enabled,
        Loop = Loop,
        Steps = Steps.Select(s => new SequencerStep { Column = s.Column, Seconds = s.Seconds }).ToList(),
    };
}

public enum ScheduleActionType { TriggerColumn, TriggerScene, StartSequencer, StopSequencer, BlackoutOn, BlackoutOff }

/// <summary>Uma regra do agendador (Fase 8 T8.5): dispara uma ação num horário/dias da semana.</summary>
public sealed class ScheduleRule
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Regra";
    public bool Enabled { get; set; } = true;
    public DayOfWeek[] Days { get; set; } = Enum.GetValues<DayOfWeek>();
    public TimeOnly Time { get; set; }
    public ScheduleActionType Action { get; set; }
    public int ColumnOrParam { get; set; }
    public Guid? SceneId { get; set; }

    /// <summary>Data (não só hora) da última execução — evita repetir a regra no mesmo minuto.</summary>
    public DateOnly? LastRunDate { get; set; }

    public ScheduleRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        Days = (DayOfWeek[])Days.Clone(),
        Time = Time,
        Action = Action,
        ColumnOrParam = ColumnOrParam,
        SceneId = SceneId,
        LastRunDate = LastRunDate,
    };
}

/// <summary>Uma pasta monitorada (Fase 8 T8.6): arquivos viram clips automaticamente numa layer.</summary>
public sealed class WatchFolderConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int LayerIndex { get; set; }
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public WatchFolderConfig Clone() => new()
    {
        Id = Id,
        LayerIndex = LayerIndex,
        Path = Path,
        Enabled = Enabled,
    };
}

/// <summary>Configuração completa do modo instalação (Fase 8), salva no projeto.</summary>
public sealed class InstallationConfig
{
    public SequencerConfig Sequencer { get; set; } = new();
    public List<ScheduleRule> ScheduleRules { get; set; } = [];
    public List<WatchFolderConfig> WatchFolders { get; set; } = [];
    public bool AutostartEnabled { get; set; }

    public InstallationConfig Clone() => new()
    {
        Sequencer = Sequencer.Clone(),
        ScheduleRules = ScheduleRules.Select(r => r.Clone()).ToList(),
        WatchFolders = WatchFolders.Select(w => w.Clone()).ToList(),
        AutostartEnabled = AutostartEnabled,
    };
}
