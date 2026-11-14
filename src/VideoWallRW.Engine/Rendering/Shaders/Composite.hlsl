// Composite.hlsl — Fase 1
// Passe 1: desenha textura do clip como quad (com letterbox via UVs ajustadas em cbuffer)
// Passe 2: idem, mas a textura é o render target da composição

cbuffer QuadParams : register(b0)
{
    row_major float4x4 uvTransform; // matriz System.Numerics enviada em row-major
    float    opacity;
    int      blendMode;     // 0=Normal, 1=Add, 2=Multiply, 3=Screen
    float2   _pad;
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
    // Para System.Numerics.Matrix4x4 (Row-Major), o correto é mul(vetor, matriz).
    float4 uvh  = mul(float4(uv, 0, 1), uvTransform);
    o.uv        = uvh.xy;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float4 col = gTexture.Sample(gSampler, i.uv);
    
    // Premultiplied alpha default
    col.rgb *= col.a;
    
    // Multiply BlendMode: src_color * dest_color.
    // Para fade-out, deve tender a branco (1.0) para que branco * dest_color mantenha o fundo intocado.
    if (blendMode == 2)
    {
        col.rgb = lerp(float3(1, 1, 1), col.rgb, opacity);
        return col; // alpha irrelevante no multiply, será omitido pelo hardware blending state.
    }
    
    col.rgb *= opacity;
    col.a   *= opacity;
    
    return col;
}
