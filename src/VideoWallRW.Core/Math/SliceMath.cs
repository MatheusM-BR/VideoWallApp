using System.Numerics;
using VideoWallRW.Core.Model;

namespace VideoWallRW.Core.Math;

/// <summary>
/// Matemática pura (sem GPU) do fatiamento de saída (Fase 4). Dado um slice e os
/// tamanhos envolvidos, produz duas matrizes afins (convenção linha-vetor, o shader
/// faz <c>mul(float4(local,0,1), M)</c>) para o quad unitário local em [0,1]×[0,1]:
///
/// <list type="bullet">
/// <item><see cref="Compute"/>.Position — local → NDC do backbuffer do monitor,
/// posicionando o quad no <see cref="Slice.OutputRect"/>.</item>
/// <item><see cref="Compute"/>.Uv — local → UV normalizado [0,1] da composição,
/// amostrando o <see cref="Slice.InputRect"/> com rotação e flips aplicados.</item>
/// </list>
///
/// As matrizes são compostas via multiplicação de matrizes simples (escala/rotação/
/// translação) em vez de álgebra derivada à mão por caso — reduz risco de erro nos
/// 16 combos de rotação×flipH×flipV.
/// </summary>
public static class SliceMath
{
    public static (Matrix4x4 Position, Matrix4x4 Uv) Compute(
        Slice slice, float compWidth, float compHeight, float monitorWidth, float monitorHeight)
    {
        return (ComputePosition(slice.OutputRect, monitorWidth, monitorHeight),
                ComputeUv(slice, compWidth, compHeight));
    }

    /// <summary>Mapeia o quad unitário para a posição/tamanho do OutputRect, em NDC.</summary>
    public static Matrix4x4 ComputePosition(RectF outputRect, float monitorWidth, float monitorHeight)
    {
        if (monitorWidth <= 0 || monitorHeight <= 0)
            return Matrix4x4.Identity;

        float scaleX = outputRect.Width / monitorWidth * 2f;
        float scaleY = -(outputRect.Height / monitorHeight * 2f); // Y de tela cresce p/ baixo; NDC cresce p/ cima
        float offsetX = outputRect.X / monitorWidth * 2f - 1f;
        float offsetY = 1f - outputRect.Y / monitorHeight * 2f;

        var m = Matrix4x4.Identity;
        m.M11 = scaleX;
        m.M22 = scaleY;
        m.M41 = offsetX;
        m.M42 = offsetY;
        return m;
    }

    /// <summary>Mapeia o quad unitário (após rotação/flip) para UV normalizado dentro do InputRect.</summary>
    public static Matrix4x4 ComputeUv(Slice slice, float compWidth, float compHeight)
    {
        if (compWidth <= 0 || compHeight <= 0)
            return Matrix4x4.Identity;

        var rotate = RotationMatrix(slice.Rotation);
        var flip = FlipMatrix(slice.FlipH, slice.FlipV);
        var rect = RectMapMatrix(slice.InputRect, compWidth, compHeight);

        // Convenção linha-vetor: aplicar rotação, depois flip, depois mapear pro retângulo
        // corresponde a v*(rotate*flip*rect) — associatividade da multiplicação de matrizes.
        return Matrix4x4.Multiply(Matrix4x4.Multiply(rotate, flip), rect);
    }

    /// <summary>local(lx,ly) em [0,1]² → (u,v) em [0,1]² rotacionado, antes do flip/rect-map.</summary>
    private static Matrix4x4 RotationMatrix(Rotation rotation)
    {
        var m = Matrix4x4.Identity;
        switch (rotation)
        {
            case Rotation.R0:
                // u=lx, v=ly (identidade)
                break;
            case Rotation.R90:
                // u=ly, v=1-lx
                m.M11 = 0; m.M21 = 1; // u = 0*lx + 1*ly
                m.M12 = -1; m.M22 = 0; // v = -1*lx + 0*ly
                m.M41 = 0; m.M42 = 1; // + (0, 1)
                break;
            case Rotation.R180:
                // u=1-lx, v=1-ly
                m.M11 = -1; m.M22 = -1;
                m.M41 = 1; m.M42 = 1;
                break;
            case Rotation.R270:
                // u=1-ly, v=lx
                m.M11 = 0; m.M21 = -1;
                m.M12 = 1; m.M22 = 0;
                m.M41 = 1; m.M42 = 0;
                break;
        }
        return m;
    }

    private static Matrix4x4 FlipMatrix(bool flipH, bool flipV)
    {
        var m = Matrix4x4.Identity;
        if (flipH) { m.M11 = -1; m.M41 = 1; }
        if (flipV) { m.M22 = -1; m.M42 = 1; }
        return m;
    }

    private static Matrix4x4 RectMapMatrix(RectF inputRect, float compWidth, float compHeight)
    {
        var m = Matrix4x4.Identity;
        m.M11 = inputRect.Width / compWidth;
        m.M22 = inputRect.Height / compHeight;
        m.M41 = inputRect.X / compWidth;
        m.M42 = inputRect.Y / compHeight;
        return m;
    }

    /// <summary>
    /// Aplica uma matriz afim a um ponto local — mesma convenção linha-vetor que o shader
    /// usa em <c>mul(float4(local,0,1), M)</c> (row_major). Implementado manualmente (não via
    /// <see cref="Vector4.Transform"/>) para não depender da convenção interna do BCL.
    /// </summary>
    public static Vector2 TransformPoint(Matrix4x4 m, Vector2 local)
    {
        float x = local.X * m.M11 + local.Y * m.M21 + m.M41;
        float y = local.X * m.M12 + local.Y * m.M22 + m.M42;
        return new Vector2(x, y);
    }
}
