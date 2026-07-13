using System.Numerics;
using System.Runtime.InteropServices;
using Serilog;
using VideoWallRW.Core;
using VideoWallRW.Engine.Graphics;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace VideoWallRW.Engine.Rendering;

/// <summary>
/// Renderiza a composição (camadas → render target) e projeta para cada saída.
/// Fase 1: uma única fonte de vídeo, resize mode Fit (letterbox).
/// </summary>
public sealed class CompositionRenderer : IDisposable
{
    // Dimensões da composição (fixas na Fase 1; configuráveis a partir da Fase 2).
    public const int CompWidth = 1920;
    public const int CompHeight = 1080;

    private readonly GraphicsDevice _gfx;
    private readonly ID3D11Texture2D _compTexture;
    private readonly ID3D11RenderTargetView _compRtv;
    private readonly ID3D11ShaderResourceView _compSrv;

    private readonly ID3D11VertexShader _vs;
    private readonly ID3D11PixelShader _ps;
    private readonly ID3D11Buffer _cbuffer;
    private readonly ID3D11SamplerState _sampler;

    [StructLayout(LayoutKind.Sequential)]
    private struct QuadParams
    {
        public Matrix4x4 UvTransform;
        public float Opacity;
        public float Pad0, Pad1, Pad2;
    }

    public CompositionRenderer(GraphicsDevice gfx)
    {
        _gfx = gfx;

        // Render target da composição.
        var texDesc = new Texture2DDescription
        {
            Width = CompWidth,
            Height = CompHeight,
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

        // CBuffer (QuadParams: 16+16+16+4 = alinhado a 16 bytes).
        _cbuffer = gfx.Device.CreateBuffer(new BufferDescription(
            (uint)Marshal.SizeOf<QuadParams>(), BindFlags.ConstantBuffer));

        // Sampler linear clamp.
        _sampler = gfx.Device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagMipLinear,
            TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));

        Log.Debug("CompositionRenderer criado ({W}×{H})", CompWidth, CompHeight);
    }

    /// <summary>
    /// Passe 1: composição — desenha o clip no render target interno.
    /// Passe 2: projeção — copia a composição para cada saída (RTV do backbuffer).
    /// </summary>
    public void Render(
        ID3D11DeviceContext ctx,
        IFrameSource? clip, double playhead,
        IReadOnlyList<(ID3D11RenderTargetView Rtv, int Width, int Height)> outputs)
    {
        // ── Passe 1: composição ───────────────────────────────────────
        ctx.ClearRenderTargetView(_compRtv, new Color4(0, 0, 0, 1));
        ctx.OMSetRenderTargets(_compRtv);
        ctx.RSSetViewport(new Viewport(0, 0, CompWidth, CompHeight));

        var srv = clip?.AcquireFrame(playhead);
        if (srv != null && clip != null)
        {
            // UV transform para Fit (letterbox): manter proporção do clip.
            var uvTransform = ComputeFitTransform(clip.Width, clip.Height, CompWidth, CompHeight);
            DrawQuad(ctx, srv, uvTransform, 1.0f);
        }

        // ── Passe 2: saídas ──────────────────────────────────────────
        foreach (var (rtv, w, h) in outputs)
        {
            ctx.OMSetRenderTargets(rtv);
            ctx.RSSetViewport(new Viewport(0, 0, w, h));
            // Composição esticada para cobrir toda a saída (slices vêm na Fase 4).
            DrawQuad(ctx, _compSrv, Matrix4x4.Identity, 1.0f);
        }
    }

    private void DrawQuad(ID3D11DeviceContext ctx,
        ID3D11ShaderResourceView srv, Matrix4x4 uvTransform, float opacity)
    {
        var p = new QuadParams { UvTransform = uvTransform, Opacity = opacity };
        ctx.UpdateSubresource(p, _cbuffer);
        ctx.IASetInputLayout(null);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _cbuffer);
        ctx.PSSetShaderResource(0, srv);
        ctx.PSSetSampler(0, _sampler);
        ctx.Draw(3, 0);
    }

    /// <summary>
    /// Calcula a matriz UV para "Fit" (letterbox): mantém a proporção do clip
    /// inserindo barras pretas nas laterais ou no topo/baixo.
    /// </summary>
    private static Matrix4x4 ComputeFitTransform(
        int srcW, int srcH, int dstW, int dstH)
    {
        float srcAspect = (float)srcW / srcH;
        float dstAspect = (float)dstW / dstH;

        float scaleU, scaleV, offsetU, offsetV;
        if (srcAspect > dstAspect)
        {
            // Pillarbox: barras em cima/baixo.
            scaleU = 1f;
            scaleV = srcAspect / dstAspect;
            offsetU = 0f;
            offsetV = (1f - scaleV) * 0.5f;
        }
        else
        {
            // Letterbox: barras nas laterais.
            scaleU = dstAspect / srcAspect;
            scaleV = 1f;
            offsetU = (1f - scaleU) * 0.5f;
            offsetV = 0f;
        }

        // Constrói uma matriz que mapeia UV [0,1]² → [offset, offset+scale].
        // O shader faz: uvh = mul(uvTransform, float4(uv, 0, 1)).xy
        var m = Matrix4x4.Identity;
        m.M11 = scaleU;
        m.M22 = scaleV;
        m.M41 = offsetU; // translação em UV (linha 4 = coluna de homogêneo)
        m.M42 = offsetV;
        return m;
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _cbuffer.Dispose();
        _ps.Dispose();
        _vs.Dispose();
        _compSrv.Dispose();
        _compRtv.Dispose();
        _compTexture.Dispose();
    }

    private static byte[] CompileShader(string source, string entryPoint, string profile)
    {
        Compiler.Compile(source, entryPoint, "Composite.hlsl", profile, out Blob blob, out Blob errorBlob);
        using (errorBlob)
        {
            if (blob is null)
                throw new InvalidOperationException(
                    $"Falha ao compilar shader {entryPoint}: {errorBlob?.AsString()}");
        }
        using (blob) return blob.AsBytes();
    }
}
