using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;

namespace VideoWallRW.Engine.Rendering;

/// <summary>
/// Cena de teste da fase 0: gradiente animado + barra branca em movimento
/// (a barra é o detector visual de frame drop — qualquer soluço vira um salto).
/// Fullscreen triangle via SV_VertexID: sem vertex buffer nem input layout.
/// </summary>
public sealed class TestSceneRenderer : IDisposable
{
    private const string ShaderSource = """
        cbuffer Params : register(b0)
        {
            float iTime;
            float2 iResolution;
            float _pad;
        };

        struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };

        VSOut VSMain(uint id : SV_VertexID)
        {
            VSOut o;
            float2 uv = float2((id << 1) & 2, id & 2);
            o.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
            o.uv = uv;
            return o;
        }

        float4 PSMain(VSOut i) : SV_Target
        {
            float3 col = 0.5 + 0.5 * cos(iTime + i.uv.xyx * 4.0 + float3(0, 2, 4));
            float barX = frac(iTime * 0.15);
            float halfWidthUv = 2.0 / iResolution.x;
            if (abs(i.uv.x - barX) < halfWidthUv)
                col = float3(1, 1, 1);
            return float4(col, 1);
        }
        """;

    [StructLayout(LayoutKind.Sequential)]
    private struct Params
    {
        public float Time;
        public Vector2 Resolution;
        public float Pad;
    }

    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11Buffer _constantBuffer;

    public TestSceneRenderer(Graphics.GraphicsDevice gfx)
    {
        _vertexShader = gfx.Device.CreateVertexShader(Compile("VSMain", "vs_5_0"));
        _pixelShader = gfx.Device.CreatePixelShader(Compile("PSMain", "ps_5_0"));
        _constantBuffer = gfx.Device.CreateBuffer(
            new BufferDescription(16, BindFlags.ConstantBuffer));
    }

    public void Render(ID3D11DeviceContext context, ID3D11RenderTargetView rtv,
        int width, int height, double timeSeconds)
    {
        var parameters = new Params
        {
            Time = (float)timeSeconds,
            Resolution = new Vector2(width, height),
        };
        context.UpdateSubresource(parameters, _constantBuffer);
        context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));
        context.OMSetRenderTargets(rtv);
        context.RSSetViewport(new Viewport(0, 0, width, height));
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.VSSetShader(_vertexShader);
        context.PSSetShader(_pixelShader);
        context.PSSetConstantBuffer(0, _constantBuffer);
        context.Draw(3, 0);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _pixelShader.Dispose();
        _vertexShader.Dispose();
    }

    private static byte[] Compile(string entryPoint, string profile)
    {
        Compiler.Compile(ShaderSource, entryPoint, "TestScene.hlsl", profile,
            out Blob blob, out Blob errorBlob);
        using (errorBlob)
        {
            if (blob is null)
                throw new InvalidOperationException(
                    $"Falha ao compilar shader {entryPoint}: {errorBlob?.AsString()}");
        }
        using (blob)
            return blob.AsBytes();
    }
}
