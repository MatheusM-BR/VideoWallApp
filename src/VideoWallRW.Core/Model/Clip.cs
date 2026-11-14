namespace VideoWallRW.Core.Model;

public sealed class Clip
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public SourceDescriptor Source { get; set; }

    public PlayMode PlayMode { get; set; } = PlayMode.Loop;
    public double Speed { get; set; } = 1.0;
    public double? InPoint { get; set; }
    public double? OutPoint { get; set; }
    public double? ImageDuration { get; set; }

    /// <summary>Duração total do arquivo em segundos (sondada uma vez no import,
    /// persistida no projeto) — alimenta a barra de trim e os defaults de In/Out.</summary>
    public double? DurationSeconds { get; set; }

    public ResizeMode? Resize { get; set; } // null = herda da Layer

    /// <summary>Padrão da operação: todo vídeo novo entra com volume ZERADO —
    /// o operador sobe o volume só no clip que deve soar (telão costuma ser mudo).</summary>
    public double Volume { get; set; } = 0.0;
    public bool Mute { get; set; }

    /// <summary>Padrão da operação: toda mídia nova já entra avançando pro próximo
    /// clip ao terminar (fotos após o timer, vídeos após cada passada).</summary>
    public AutopilotAction Autopilot { get; set; } = AutopilotAction.PlayNextClip;

    // ── Transform manual (painel CLIP interativo) ──────────────────────────
    // Aplicado POR CIMA do retângulo calculado pelo Resize (Fit/Fill/etc.):
    // primeiro o Resize encaixa a mídia na composição, depois este transform
    // escala/move o resultado. A maioria dos clipes nunca mexe nisso (fica
    // no padrão PositionX=PositionY=0, ScaleX=ScaleY=1).

    /// <summary>Deslocamento horizontal, em frações da largura da composição
    /// (-1 = uma tela inteira pra esquerda, 0 = centrado, 1 = uma tela inteira pra direita).</summary>
    public double PositionX { get; set; } = 0.0;

    /// <summary>Deslocamento vertical, em frações da altura da composição.</summary>
    public double PositionY { get; set; } = 0.0;

    /// <summary>Escala horizontal aplicada sobre o retângulo do Resize (1 = tamanho normal).</summary>
    public double ScaleX { get; set; } = 1.0;

    /// <summary>Escala vertical aplicada sobre o retângulo do Resize (1 = tamanho normal).</summary>
    public double ScaleY { get; set; } = 1.0;

    /// <summary>Grupo de troca (T20): células com o mesmo Id aqui trocam juntas — editar
    /// uma (mídia, duração, resize, autopilot, transform...) propaga pras outras do
    /// grupo. Null = célula independente, comportamento normal.</summary>
    public Guid? LinkGroupId { get; set; }

    public Clip(SourceDescriptor source)
    {
        Source = source;
    }

    /// <summary>
    /// Mesmo conteúdo, mas com um ID ESCOLHIDO. Usado na propagação de grupos de troca:
    /// a célula destino recebe a mídia/configuração da origem mas MANTÉM a identidade
    /// dela. Sem isso, cada propagação trocava o Id do clip da célula e o autopilot
    /// perdia a coluna atual (Array.FindIndex → -1), voltando pra coluna 0 em vez de
    /// avançar — e o UpdateClip não casava mais com o clip no ar.
    /// </summary>
    public Clip CloneWithId(Guid id)
    {
        var copy = Clone();
        return new Clip(copy.Source)
        {
            Id = id,
            Name = copy.Name,
            PlayMode = copy.PlayMode,
            Speed = copy.Speed,
            InPoint = copy.InPoint,
            OutPoint = copy.OutPoint,
            ImageDuration = copy.ImageDuration,
            DurationSeconds = copy.DurationSeconds,
            Resize = copy.Resize,
            Volume = copy.Volume,
            Mute = copy.Mute,
            Autopilot = copy.Autopilot,
            PositionX = copy.PositionX,
            PositionY = copy.PositionY,
            ScaleX = copy.ScaleX,
            ScaleY = copy.ScaleY,
            LinkGroupId = copy.LinkGroupId,
        };
    }

    /// <summary>Criar um deep clone (novo ID) caso seja duplicado.</summary>
    public Clip Clone()
    {
        return new Clip(Source)
        {
            Id = Guid.NewGuid(),
            Name = Name,
            PlayMode = PlayMode,
            Speed = Speed,
            InPoint = InPoint,
            OutPoint = OutPoint,
            ImageDuration = ImageDuration,
            DurationSeconds = DurationSeconds,
            Resize = Resize,
            Volume = Volume,
            Mute = Mute,
            Autopilot = Autopilot,
            PositionX = PositionX,
            PositionY = PositionY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            LinkGroupId = LinkGroupId,
        };
    }

    /// <summary>Cópia para sincronização: mantém a identidade do clip entre UI e engine.</summary>
    public Clip Copy()
    {
        return new Clip(Source)
        {
            Id = Id,
            Name = Name,
            PlayMode = PlayMode,
            Speed = Speed,
            InPoint = InPoint,
            OutPoint = OutPoint,
            ImageDuration = ImageDuration,
            DurationSeconds = DurationSeconds,
            Resize = Resize,
            Volume = Volume,
            Mute = Mute,
            Autopilot = Autopilot,
            PositionX = PositionX,
            PositionY = PositionY,
            ScaleX = ScaleX,
            ScaleY = ScaleY,
            LinkGroupId = LinkGroupId,
        };
    }
}
