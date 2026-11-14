namespace VideoWallRW.Core.Model;

public sealed class Layer
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;

    public BlendMode Blend { get; set; } = BlendMode.Normal;
    public double Opacity { get; set; } = 1.0;
    public bool Bypass { get; set; }
    public bool Solo { get; set; }

    public double TransitionSeconds { get; set; } = 0.0;
    public ResizeMode DefaultResize { get; set; } = ResizeMode.Fit;

    /// <summary>Autopilot da layer LIGADO/DESLIGADO: desligado, os clips que terminam
    /// seguram no último frame (loop continua loopando) e nada avança sozinho.</summary>
    public bool AutopilotEnabled { get; set; } = true;

    /// <summary>
    /// Layer FIXA: atalho/disparo de COLUNA inteira pula esta layer. Serve pra linha
    /// de entrada ao vivo (câmera NDI, por exemplo) que deve continuar no ar enquanto
    /// o operador troca as colunas de conteúdo — sem isto, apertar o atalho da coluna
    /// derrubava o sinal ao vivo junto. Disparar a célula direto continua funcionando.
    /// </summary>
    public bool IgnoreColumnTrigger { get; set; }

    /// <summary>
    /// Matriz de clipes. O tamanho do array deve ser mantido sempre igual ao ColumnCount
    /// definido na composição owner. Array null significa célula vazia.
    /// </summary>
    public Clip?[] Clips { get; set; } = [];

    public Layer Clone()
    {
        var l = new Layer
        {
            Id = Guid.NewGuid(),
            Name = Name,
            Blend = Blend,
            Opacity = Opacity,
            Bypass = Bypass,
            Solo = Solo,
            TransitionSeconds = TransitionSeconds,
            DefaultResize = DefaultResize,
            AutopilotEnabled = AutopilotEnabled,
            IgnoreColumnTrigger = IgnoreColumnTrigger,
            Clips = new Clip?[Clips.Length]
        };
        for (int i = 0; i < Clips.Length; i++)
            l.Clips[i] = Clips[i]?.Clone();
        return l;
    }

    /// <summary>Cópia para sincronização: mantém IDs estáveis para updates incrementais.</summary>
    public Layer Copy()
    {
        var copy = new Layer
        {
            Id = Id,
            Name = Name,
            Blend = Blend,
            Opacity = Opacity,
            Bypass = Bypass,
            Solo = Solo,
            TransitionSeconds = TransitionSeconds,
            DefaultResize = DefaultResize,
            AutopilotEnabled = AutopilotEnabled,
            IgnoreColumnTrigger = IgnoreColumnTrigger,
            Clips = new Clip?[Clips.Length]
        };
        for (int i = 0; i < Clips.Length; i++)
            copy.Clips[i] = Clips[i]?.Copy();
        return copy;
    }
}
