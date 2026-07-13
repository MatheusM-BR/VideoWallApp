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
    private readonly Graphics.GraphicsDevice _gfx;
    private readonly IDXGISwapChain1 _swapChain;
    private readonly bool _tearingSupported;

    public OutputWindow Window { get; }
    public MonitorInfo Monitor => Window.Monitor;
    public ID3D11RenderTargetView Rtv { get; private set; }
    public bool IsPrimary { get; set; }

    public SwapChainOutput(Graphics.GraphicsDevice gfx, MonitorInfo monitor)
    {
        _gfx = gfx;
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

            Rtv = CreateBackBufferRtv();
        }
        catch
        {
            Window.Dispose();
            throw;
        }
    }

    private ID3D11RenderTargetView CreateBackBufferRtv()
    {
        using var backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        return _gfx.Device.CreateRenderTargetView(backBuffer);
    }

    public Result Present()
    {
        Result result;
        if (IsPrimary)
            result = _swapChain.Present(1, PresentFlags.None);
        else
            result = _tearingSupported
                ? _swapChain.Present(0, PresentFlags.AllowTearing)
                : _swapChain.Present(0, PresentFlags.None);

        if (result.Success)
        {
            // Swapchains flip-model (FLIP_DISCARD) rotam qual buffer físico o índice 0
            // representa a cada Present — o RTV precisa ser recriado a cada frame, senão
            // o render passa a escrever num buffer obsoleto e a saída trava preta.
            Rtv.Dispose();
            Rtv = CreateBackBufferRtv();
        }
        return result;
    }

    public void Dispose()
    {
        Rtv.Dispose();
        _swapChain.Dispose();
        Window.Dispose();
    }
}
