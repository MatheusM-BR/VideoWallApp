using Serilog;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Direct3D11.Debug;
using Vortice.DXGI;

namespace VideoWallRW.Engine.Graphics;

/// <summary>
/// Device D3D11 compartilhado por todo o engine. O <see cref="Context"/> (immediate)
/// só pode ser usado pela render thread.
/// </summary>
public sealed class GraphicsDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public bool TearingSupported { get; }
    public FeatureLevel FeatureLevel { get; }

    private GraphicsDevice(ID3D11Device device, ID3D11DeviceContext context,
        IDXGIFactory2 factory, bool tearingSupported, FeatureLevel featureLevel)
    {
        Device = device;
        Context = context;
        Factory = factory;
        TearingSupported = tearingSupported;
        FeatureLevel = featureLevel;
    }

    public static GraphicsDevice Create()
    {
        var flags = DeviceCreationFlags.BgraSupport;
#if DEBUG
        // Camada de debug só existe com as Graphics Tools instaladas — fallback silencioso.
        if (D3D11.SdkLayersAvailable())
            flags |= DeviceCreationFlags.Debug;
#endif
        FeatureLevel[] featureLevels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, featureLevels,
            out ID3D11Device? device, out FeatureLevel achieved, out ID3D11DeviceContext? context).CheckError();

        // Obrigatório para o D3D11VA da fase 6; ligado desde já (barato).
        using (var multithread = context!.QueryInterfaceOrNull<ID3D11Multithread>())
            multithread?.SetMultithreadProtected(true);

        using var dxgiDevice = device!.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        var factory = adapter.GetParent<IDXGIFactory2>();

        bool tearing = false;
        using (var factory5 = factory.QueryInterfaceOrNull<IDXGIFactory5>())
            tearing = factory5?.PresentAllowTearing ?? false;

        Log.Information("D3D11 device criado: {Adapter}, feature level {Level}, tearing {Tearing}",
            adapter.Description.Description, achieved, tearing);
        return new GraphicsDevice(device, context, factory, tearing, achieved);
    }

    public void Dispose()
    {
        Context.ClearState();
        Context.Flush();
        Context.Dispose();
        Factory.Dispose();
#if DEBUG
        using (var debug = Device.QueryInterfaceOrNull<ID3D11Debug>())
            debug?.ReportLiveDeviceObjects(ReportLiveDeviceObjectFlags.Detail | ReportLiveDeviceObjectFlags.IgnoreInternal);
#endif
        Device.Dispose();
    }
}
