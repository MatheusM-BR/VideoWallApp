namespace VideoWallRW.Engine.Output;

/// <summary>
/// Monitor físico em coordenadas de pixels FÍSICOS do desktop (DXGI).
/// <paramref name="DeviceName"/> (ex.: <c>\\.\DISPLAY1</c>) é o id estável.
/// </summary>
public sealed record MonitorInfo(string DeviceName, int X, int Y, int Width, int Height, string AdapterDescription)
{
    public override string ToString() => $"{DeviceName} {Width}x{Height} @ ({X},{Y})";
}
