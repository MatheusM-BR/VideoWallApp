using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using VideoWallRW.Engine;
using VideoWallRW.Engine.Graphics;
using Vortice.Direct3D11;
using Vortice.WIC;
using Vortice.DXGI;

namespace VideoWallRW.Media.Sources;

/// <summary>
/// Fonte de imagem estática. Decodifica via WIC de forma assíncrona.
/// </summary>
public sealed class ImageFileSource : IPlayableSource
{
    private readonly GraphicsDevice _gfx;
    private readonly string _path;
    
    private ID3D11Texture2D? _texture;
    private ID3D11ShaderResourceView? _srv;
    
    private volatile SourceState _state = SourceState.Arming;
    private volatile string? _error;

    private int _width;
    private int _height;
    private bool _endReachedFired;

    public double Duration { get; set; } = 10.0;
    
    public double DecodeFps => 0; // Estático
    public SourceState State => _state;
    public string? ErrorMessage => _error;
    public int Width => _width;
    public int Height => _height;
    
    public bool SupportsSeek => false;
    public event Action? EndReached;

    public ImageFileSource(GraphicsDevice gfx, string path)
    {
        _gfx = gfx;
        _path = path;
        
        // Dispara carga assíncrona para não travar a UI/Engine
        Task.Run(LoadImageAsync);
    }

    private void LoadImageAsync()
    {
        try
        {
            Log.Debug("Carregando imagem {Path}", _path);
            
            using var factory = new IWICImagingFactory();
            using var decoder = factory.CreateDecoderFromFileName(_path);
            using var frame = decoder.GetFrame(0);
            
            _width = (int)frame.Size.Width;
            _height = (int)frame.Size.Height;
            
            using var converter = factory.CreateFormatConverter();
            converter.Initialize(frame, PixelFormat.Format32bppPBGRA, BitmapDitherType.None, null, 0.0, BitmapPaletteType.Custom);
            
            int stride = _width * 4;
            int bufferSize = stride * _height;
            byte[] pixels = new byte[bufferSize];
            converter.CopyPixels((uint)stride, pixels);
            
            // Requer subir na GPU, que pode brigar com Context principal do RenderThread,
            // entretanto CreateTexture2D trabalha no Device que é thread-safe no D3D11!
            var desc = new Texture2DDescription
            {
                Width = (uint)_width,
                Height = (uint)_height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Immutable,
                BindFlags = BindFlags.ShaderResource
            };
            
            unsafe
            {
                fixed (byte* p = pixels)
                {
                    var data = new Vortice.Direct3D11.SubresourceData((nint)p, (uint)stride, (uint)bufferSize);
                    _texture = _gfx.Device.CreateTexture2D(desc, [data]);
                }
            }
            
            _srv = _gfx.Device.CreateShaderResourceView(_texture);
            
            _state = SourceState.Playing;
            Log.Information("Imagem carregada: {Path} ({W}x{H})", _path, _width, _height);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _state = SourceState.Error;
            Log.Error(ex, "Falha ao decodificar imagem {Path}", _path);
        }
    }

    public ID3D11ShaderResourceView? AcquireFrame(double playheadSeconds)
    {
        if (_state == SourceState.Error) return null;
        
        if (!_endReachedFired && _state == SourceState.Playing && playheadSeconds >= Duration)
        {
            _endReachedFired = true;
            EndReached?.Invoke();
        }
        
        return _srv;
    }

    public void Seek(double seconds) {}
    public void Start() {}
    public void Stop() {}

    public void Dispose()
    {
        _srv?.Dispose();
        _texture?.Dispose();
        _srv = null;
        _texture = null;
    }
}
