using VideoWallRW.Core.Math;

namespace VideoWallRW.Core.Model;

public enum Rotation { R0, R90, R180, R270 }

public sealed class ColorCorrection
{
    public double Brightness { get; set; } = 0.0; // -1..1
    public double Contrast { get; set; } = 1.0;   // 0..2
    public double Gamma { get; set; } = 1.0;      // 0.1..4
}

/// <summary>
/// Um recorte da composição desenhado num retângulo do monitor de saída.
/// <see cref="InputRect"/> é em pixels do espaço da composição; <see cref="OutputRect"/>
/// é em pixels físicos do monitor (origem = canto superior esquerdo do monitor).
/// </summary>
public sealed class Slice
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Slice";
    public RectF InputRect { get; set; }
    public RectF OutputRect { get; set; }
    public Rotation Rotation { get; set; } = Rotation.R0;
    public bool FlipH { get; set; }
    public bool FlipV { get; set; }

    public Slice Clone() => new()
    {
        Id = Id,
        Name = Name,
        InputRect = InputRect,
        OutputRect = OutputRect,
        Rotation = Rotation,
        FlipH = FlipH,
        FlipV = FlipV,
    };
}

/// <summary>Uma saída física (monitor). <see cref="MonitorDeviceName"/> é o id estável do DXGI (fase 0).</summary>
public sealed class Screen
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "Screen";
    public string MonitorDeviceName { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public ColorCorrection Color { get; set; } = new();
    public List<Slice> Slices { get; set; } = [];

    /// <summary>Roteamento por saída (estilo Arena): null = composição inteira
    /// (todas as layers misturadas); N = só a layer de índice N vai pra este monitor.</summary>
    public int? SourceLayerIndex { get; set; }

    public Screen Clone() => new()
    {
        Id = Id,
        Name = Name,
        MonitorDeviceName = MonitorDeviceName,
        Enabled = Enabled,
        Color = new ColorCorrection { Brightness = Color.Brightness, Contrast = Color.Contrast, Gamma = Color.Gamma },
        Slices = Slices.Select(s => s.Clone()).ToList(),
        SourceLayerIndex = SourceLayerIndex,
    };
}

/// <summary>Conjunto de saídas físicas + fatiamento da composição — a "Advanced Output".</summary>
public sealed class OutputSetup
{
    public List<Screen> Screens { get; set; } = [];

    public OutputSetup Clone() => new() { Screens = Screens.Select(s => s.Clone()).ToList() };

    /// <summary>
    /// Cria (ou substitui) um slice único cobrindo toda a composição e todo o monitor —
    /// o comportamento padrão de hoje (esticar a composição inteira), usado quando o
    /// operador ainda não configurou nada no editor de Advanced Output.
    /// </summary>
    public static Screen CreateFullscreenScreen(string monitorDeviceName, int compWidth, int compHeight,
        int monitorWidth, int monitorHeight)
    {
        return new Screen
        {
            Name = monitorDeviceName,
            MonitorDeviceName = monitorDeviceName,
            Enabled = true,
            Slices =
            [
                new Slice
                {
                    Name = "Full",
                    InputRect = new RectF(0, 0, compWidth, compHeight),
                    OutputRect = new RectF(0, 0, monitorWidth, monitorHeight),
                }
            ]
        };
    }
}
