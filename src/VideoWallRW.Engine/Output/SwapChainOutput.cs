using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace VideoWallRW.Engine.Output;

/// <summary>
/// Saída ativa: janela borderless + swapchain flip-model.
/// Estratégia de present: primária com vsync (regula o frame loop);
/// secundárias sem vsync (ALLOW_TEARING quando suportado) para não travar
/// o loop em monitores de taxas diferentes.
/// </summary>
public sealed class SwapChainOutput : IDisposable
{
    private readonly IDXGISwapChain1 _swapChain;
    private readonly bool _tearingSupported;

    public OutputWindow Window { get; }
    public MonitorInfo Monitor => Window.Monitor;
    public ID3D11RenderTargetView Rtv { get; }
    public bool IsPrimary { get; set; }

    public SwapChainOutput(Graphics.GraphicsDevice gfx, MonitorInfo monitor)
    {
        _tearingSupported = gfx.TearingSupported;
        Window = new OutputWindow(monitor);
        try
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)monitor.Width,
                Height = (uint)monitor.Height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = AlphaMode.Ignore,
                Flags = _tearingSupported ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
            };
            _swapChain = gfx.Factory.CreateSwapChainForHwnd(gfx.Device, Window.Hwnd, desc);
            gfx.Factory.MakeWindowAssociation(Window.Hwnd, WindowAssociationFlags.IgnoreAltEnter);

            using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            Rtv = gfx.Device.CreateRenderTargetView(backBuffer);
        }
        catch
        {
            Window.Dispose();
            throw;
        }
    }

    public Result Present()
    {
        if (IsPrimary)
            return _swapChain.Present(1, PresentFlags.None);
        return _tearingSupported
            ? _swapChain.Present(0, PresentFlags.AllowTearing)
            : _swapChain.Present(0, PresentFlags.None);
    }

    public void Dispose()
    {
        Rtv.Dispose();
        _swapChain.Dispose();
        Window.Dispose();
    }
}
