// Composite.hlsl — Fase 1
// Passe 1: desenha textura do clip como quad (com letterbox via UVs ajustadas em cbuffer)
// Passe 2: idem, mas a textura é o render target da composição

cbuffer QuadParams : register(b0)
{
    float4x4 uvTransform;   // transforma [0,1]² → [u0,v0,u1,v1] do clip na composição
    float    opacity;
    float3   _pad;
};

Texture2D    gTexture : register(t0);
SamplerState gSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

// VS fullscreen triangle via SV_VertexID (sem vertex buffer).
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    // Gera os 3 vértices que cobrem [-1,1]² em clip space.
    float2 uv   = float2((id << 1) & 2, id & 2);
    o.pos       = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    // Aplicar transformação de UV para letterbox / saída.
    // uvTransform encode: col0.xy = scale, col3.xy = offset
    float4 uvh  = mul(uvTransform, float4(uv, 0, 1));
    o.uv        = uvh.xy;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float4 col = gTexture.Sample(gSampler, i.uv);
    col.rgb    *= opacity;
    return col;
}
