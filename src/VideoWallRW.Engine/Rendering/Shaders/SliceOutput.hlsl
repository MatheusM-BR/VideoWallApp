// SliceOutput.hlsl — Fase 4
// Passe de saída avançada: desenha um retângulo (slice) da composição num
// retângulo do monitor de saída, com rotação/flip (já embutidos nas matrizes,
// calculadas por VideoWallRW.Core.Math.SliceMath) e correção de cor por tela.
// Diferente de Composite.hlsl (fullscreen triangle), aqui o quad é posicionado
// livremente — precisa de 4 vértices (TriangleStrip) e duas matrizes (posição e UV).

cbuffer SliceParams : register(b0)
{
    row_major float4x4 positionTransform; // quad unitário -> NDC do backbuffer
    row_major float4x4 uvTransform;       // quad unitário -> UV normalizado da composição
    float brightness;  // -1..1
    float contrast;    // 0..2
    float invGamma;    // 1/gamma
    float masterOpacity; // master fader / blackout (0..1)
};

Texture2D    gTexture : register(t0);
SamplerState gSampler : register(s0);

struct VSOut
{
    float4 pos : SV_Position;
    float2 uv  : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID)
{
    // TriangleStrip de 4 vértices: id 0,1,2,3 -> (0,0),(1,0),(0,1),(1,1) em espaço local.
    float2 local = float2(id & 1, (id >> 1) & 1);

    VSOut o;
    float4 posH = mul(float4(local, 0, 1), positionTransform);
    o.pos = float4(posH.xy, 0, 1);
    float4 uvH = mul(float4(local, 0, 1), uvTransform);
    o.uv = uvH.xy;
    return o;
}

float4 PSMain(VSOut i) : SV_Target
{
    float4 c = gTexture.Sample(gSampler, i.uv);
    c.rgb = pow(saturate(c.rgb * contrast + brightness), invGamma);
    c.rgb *= masterOpacity;
    c.a = 1;
    return c;
}
