namespace VideoWallRW.Core.Model;

public enum AutopilotAction
{
    None,
    PlayNextClip,
    PlayPreviousClip,
    PlayRandomClip,
    PlayFirstClip
}

public enum BlendMode
{
    Normal,
    Add,
    Multiply,
    Screen
}

public enum PlayMode
{
    Loop,
    PingPong,
    OnceHold,
    OnceClear
}

public enum ResizeMode
{
    Fit,      // Letterbox
    Fill,     // Crop (Zoom para preencher tela)
    Stretch,  // Deformar ignorando aspect ratio
    Original  // Tamanho original escala 1:1, centralizado no monitor
}
