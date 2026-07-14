using System.Numerics;
using System.Runtime.InteropServices;
using Serilog;
using VideoWallRW.Core.Math;
using VideoWallRW.Core.Model;
using VideoWallRW.Engine.Graphics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VideoWallRW.Engine.Rendering;

/// <summary>
/// Renderiza a composição baseada na Grade de camadas. 
/// Aplica as matrizes de Scale calculadas do ResizeMath para caber nas configurações da Tela,
/// processando todas as Layers utilizando Vortice BlendStates para alcançar efeito 
/// gráfico avançado (Resolume-like).
/// </summary>
public sealed class CompositionRenderer : IDisposable
{
    private readonly GraphicsDevice _gfx;
    private readonly ID3D11Texture2D _compTexture;
    private readonly ID3D11RenderTargetView _compRtv;
    private readonly ID3D11ShaderResourceView _compSrv;

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _cbuffer;
    private readonly ID3D11SamplerState _sampler;

    // Passe de saída por slices (Fase 4) — shader e sampler dedicados; ver SliceOutput.hlsl.
    private readonly ID3D11VertexShader _sliceVs;
    private readonly ID3D11PixelShader _slicePs;
    private readonly ID3D11Buffer _sliceCbuffer;
    private readonly ID3D11SamplerState _sliceSampler;
    private readonly IdentityCardRenderer _identityCards;

    // Blend States. Null state atua como pre-mul default opaco ou normal alpha estrito sob pre-mul.
    // Usaremos as flags equivalentes solicitadas pela interface.
    private readonly ID3D11BlendState _blendNormal;
    private readonly ID3D11BlendState _blendAdd;
    private readonly ID3D11BlendState _blendScreen;
    private readonly ID3D11BlendState _blendMultiply;
    
    [StructLayout(LayoutKind.Sequential)]
    private struct QuadParams
    {
        public Matrix4x4 UvTransform;
        public float Opacity;
        public int BlendMode;
        public float Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SliceParams
    {
        public Matrix4x4 PositionTransform;
        public Matrix4x4 UvTransform;
        public float Brightness;
        public float Contrast;
        public float InvGamma;
        public float MasterOpacity;
    }

    public CompositionRenderer(GraphicsDevice gfx, int width, int height)
    {
        _gfx = gfx;

        if (width < 1) throw new ArgumentOutOfRangeException(nameof(width));
        if (height < 1) throw new ArgumentOutOfRangeException(nameof(height));

        // Render target da composição (Tamanho ajustável no Engine, fixo 1080p por hora).
        var texDesc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
        };
        _compTexture = gfx.Device.CreateTexture2D(texDesc);
        _compRtv = gfx.Device.CreateRenderTargetView(_compTexture);
        _compSrv = gfx.Device.CreateShaderResourceView(_compTexture);

        // Shaders.
        string shaderSrc = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders", "Composite.hlsl"));
        _vs = gfx.Device.CreateVertexShader(CompileShader(shaderSrc, "VSMain", "vs_5_0"));
        _ps = gfx.Device.CreatePixelShader(CompileShader(shaderSrc, "PSMain", "ps_5_0"));

        _cbuffer = gfx.Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<QuadParams>(), BindFlags.ConstantBuffer));

        _sampler = gfx.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Border, TextureAddressMode.Border, TextureAddressMode.Border)
        {
            BorderColor = new Color4(0, 0, 0, 0f)
        });

        // Shader do passe de saída por slices (Fase 4).
        string sliceShaderSrc = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Rendering", "Shaders", "SliceOutput.hlsl"));
        _sliceVs = gfx.Device.CreateVertexShader(CompileShader(sliceShaderSrc, "VSMain", "vs_5_0", "SliceOutput.hlsl"));
        _slicePs = gfx.Device.CreatePixelShader(CompileShader(sliceShaderSrc, "PSMain", "ps_5_0", "SliceOutput.hlsl"));
        _sliceCbuffer = gfx.Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<SliceParams>(), BindFlags.ConstantBuffer));
        // Fora do InputRect deve sair preto opaco (é a saída final, nada mais compõe por cima) —
        // diferente do sampler da composição (Pass 1), que usa borda transparente.
        _sliceSampler = gfx.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Border, TextureAddressMode.Border, TextureAddressMode.Border)
        {
            BorderColor = new Color4(0, 0, 0, 1f)
        });
        _identityCards = new IdentityCardRenderer(gfx);

        // Blend States definition
        _blendNormal = CreateBlendState(Blend.One, Blend.InverseSourceAlpha);
        _blendAdd = CreateBlendState(Blend.One, Blend.One);
        _blendScreen = CreateBlendState(Blend.One, Blend.InverseSourceColor); // s + d - s*d => SourceAlpha == 1 and SrcColor==col, so SrcColor+(1-SrcColor)*Dest
        _blendMultiply = CreateBlendState(Blend.DestinationColor, Blend.Zero);

        Log.Debug("CompositionRenderer criado");
    }

    private ID3D11BlendState CreateBlendState(Blend src, Blend dest)
    {
        var desc = new BlendDescription
        {
            AlphaToCoverageEnable = false,
            IndependentBlendEnable = false
        };
        desc.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = src,
            DestinationBlend = dest,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.One,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        return _gfx.Device.CreateBlendState(desc);
    }

    /// <summary>
    /// Passe 1: desenha a base toda com as texturas ativas processando o blend de sobreposição per Layer.
    /// Passe 2: fatia a composição em slices por tela (Fase 4) — rotação, flip, correção de cor e
    /// o master fader/blackout são aplicados aqui, na saída, nunca na composição em si (o preview
    /// do operador deve continuar visível mesmo em blackout).
    /// </summary>
    public void Render(
        ID3D11DeviceContext ctx,
        Composition composition,
        IReadOnlyList<LayerRenderer> activeLayers,
        IReadOnlyList<(ID3D11RenderTargetView Rtv, int Width, int Height, Screen Screen)> outputs,
        bool identify = false)
    {
        ctx.ClearRenderTargetView(_compRtv, new Color4(0, 0, 0, 1));
        ctx.OMSetRenderTargets(_compRtv);
        ctx.RSSetViewport(new Viewport(0, 0, composition.Width, composition.Height));

        // Filter solo layers if any layer has Solo enabled
        bool anySolo = activeLayers.Any(l => l.Model.Solo);
        var visibleLayers = activeLayers.Where(l => !l.Model.Bypass && (!anySolo || l.Model.Solo));

        foreach (var layer in visibleLayers)
        {
            var frames = layer.AcquireFrames();
            if (frames.Count == 0 || layer.ActivePlayer is null) continue;

            double opacity = layer.Model.Opacity;

            var sourceWidth = layer.ActivePlayer.Source.Width;
            var sourceHeight = layer.ActivePlayer.Source.Height;

            var resizeMode = layer.ActiveClip?.Resize ?? layer.Model.DefaultResize;

            var rect = ResizeMath.Compute(
                resizeMode, sourceWidth, sourceHeight, composition.Width, composition.Height);

            // Compute UV projection matrix
            var uvTransform = ComputeTransform(rect, sourceWidth, sourceHeight, composition.Width, composition.Height);

            ID3D11BlendState blendState = layer.Model.Blend switch
            {
                BlendMode.Add => _blendAdd,
                BlendMode.Screen => _blendScreen,
                BlendMode.Multiply => _blendMultiply,
                _ => _blendNormal
            };

            ctx.OMSetBlendState(blendState);
            foreach (var frame in frames)
                DrawQuad(ctx, frame.Srv, uvTransform, (float)(opacity * frame.Opacity), (int)layer.Model.Blend);
        }

        // Disable generic blending before swapping outputs buffer
        ctx.OMSetBlendState(null);

        // ── Passe 2: saídas fatiadas por slice (Fase 4) ─────────────────────
        // Blackout escurece só a saída (aqui), nunca o master fader guardado —
        // desligar o blackout devolve o fader exatamente de onde estava (T7.6).
        float effectiveMasterOpacity = composition.Blackout ? 0f : (float)composition.MasterOpacity;
        var neutralColor = new ColorCorrection();
        foreach (var (rtv, w, h, screen) in outputs)
        {
            ctx.OMSetRenderTargets(rtv);
            ctx.RSSetViewport(new Viewport(0, 0, w, h));

            if (identify)
            {
                // Modo identify substitui os slices por um cartão fullscreen com nome/
                // device/resolução — não mexe no estado da grade, só na apresentação.
                var cardSrv = _identityCards.GetOrCreate(screen.MonitorDeviceName, screen.Name, screen.Slices.Count, w, h);
                var fullscreenRect = new RectF(0, 0, w, h);
                var positionM = SliceMath.ComputePosition(fullscreenRect, w, h);
                var uvM = SliceMath.ComputeUv(new Slice { InputRect = fullscreenRect }, w, h);
                DrawSlice(ctx, cardSrv, positionM, uvM, neutralColor, 1.0f);
                continue;
            }

            foreach (var slice in screen.Slices)
            {
                var (positionM, uvM) = SliceMath.Compute(slice, composition.Width, composition.Height, w, h);
                DrawSlice(ctx, _compSrv, positionM, uvM, screen.Color, effectiveMasterOpacity);
            }
        }
    }

    private void DrawSlice(ID3D11DeviceContext ctx, ID3D11ShaderResourceView srv,
        Matrix4x4 positionTransform, Matrix4x4 uvTransform, ColorCorrection color, float masterOpacity)
    {
        var p = new SliceParams
        {
            PositionTransform = positionTransform,
            UvTransform = uvTransform,
            Brightness = (float)color.Brightness,
            Contrast = (float)color.Contrast,
            InvGamma = (float)(1.0 / System.Math.Max(0.01, color.Gamma)),
            MasterOpacity = masterOpacity,
        };
        ctx.UpdateSubresource(p, _sliceCbuffer);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.VSSetShader(_sliceVs);
        ctx.PSSetShader(_slicePs);
        ctx.VSSetConstantBuffer(0, _sliceCbuffer);
        ctx.PSSetConstantBuffer(0, _sliceCbuffer);
        ctx.PSSetShaderResource(0, srv);
        ctx.PSSetSampler(0, _sliceSampler);
        ctx.Draw(4, 0);
    }

    private void DrawQuad(ID3D11DeviceContext ctx,
        ID3D11ShaderResourceView srv, Matrix4x4 uvTransform, float opacity, int blendMode)
    {
        var p = new QuadParams { UvTransform = uvTransform, Opacity = opacity, BlendMode = blendMode };
        ctx.UpdateSubresource(p, _cbuffer);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        // O VS lê uvTransform de b0 (é ele quem calcula o UV do fullscreen triangle) —
        // sem isso a matriz chega zerada no VS e todo pixel amostra o canto (0,0) da
        // textura misturado com a borda transparente, apagando a imagem quase por completo.
        ctx.VSSetConstantBuffer(0, _cbuffer);
        ctx.PSSetConstantBuffer(0, _cbuffer);
        ctx.PSSetShaderResource(0, srv);
        ctx.PSSetSampler(0, _sampler);
        ctx.Draw(3, 0);
    }

    /// <summary>
    /// Computa matriz de translação escalonada baseada na janela dada. 
    /// O Quad base no shader vai de [0,1], sendo renderizado na sua projeção máxima da Composition.
    /// Para limitar o escopo, enviamos as margens de UV que em teoria projetam a pintura no local correto.
    /// Como o cbuffer inverte a operação no shader (Scale + Translate em -UV bounds e clamp), usamos isso.
    /// </summary>
    private static Matrix4x4 ComputeTransform(RectF targetRect, int srcW, int srcH, float compW, float compH)
    {
        // Precisamos mapear o fullscreen triangle (que escreve pixel de (0,0) a (compW,compH)) 
        // para amostrar a textura src de (0,0) a (srcW,srcH) se dentro de targetRect.
        // Se pixel estiver fora de targetRect, o clamp() do sampler limitará, o edge repetition preencheria...
        // O ideal seria descartar o pixel com alpha = 0 se estivesse fora das boardas para Fill/Fit. 
        // Vamos delegar o clip p/ Clamp? ResizeMath já da Bounds exatos.
        
        float scaleU = compW / targetRect.Width;
        float scaleV = compH / targetRect.Height;
        float offsetU = -(targetRect.X / targetRect.Width);
        float offsetV = -(targetRect.Y / targetRect.Height);
        
        var m = Matrix4x4.Identity;
        m.M11 = scaleU;
        m.M22 = scaleV;
        m.M41 = offsetU; 
        m.M42 = offsetV;
        return m;
    }

    private static byte[] CompileShader(string source, string entryPoint, string profile, string fileName = "Composite.hlsl")
    {
        Compiler.Compile(source, entryPoint, fileName, profile, out Blob blob, out Blob errorBlob);
        using (errorBlob)
        {
            if (blob is null)
                throw new InvalidOperationException(
                    $"Falha ao compilar shader {entryPoint}: {errorBlob?.AsString()}");
        }
        using (blob) return blob.AsBytes();
    }

    public void Dispose()
    {
        _blendNormal.Dispose();
        _blendAdd.Dispose();
        _blendScreen.Dispose();
        _blendMultiply.Dispose();
        _sampler.Dispose();
        _cbuffer.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _sliceSampler.Dispose();
        _sliceCbuffer.Dispose();
        _slicePs.Dispose();
        _sliceVs.Dispose();
        _identityCards.Dispose();
        _compSrv.Dispose();
        _compRtv.Dispose();
        _compTexture.Dispose();
    }
}

