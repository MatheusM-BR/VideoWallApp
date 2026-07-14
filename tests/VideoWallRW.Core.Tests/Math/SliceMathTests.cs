using System.Numerics;
using VideoWallRW.Core.Math;
using VideoWallRW.Core.Model;
using Xunit;

namespace VideoWallRW.Core.Tests.Math;

public class SliceMathTests
{
    private const float Epsilon = 0.001f;

    private static void AssertVector2(Vector2 expected, Vector2 actual)
    {
        Assert.True(System.Math.Abs(expected.X - actual.X) < Epsilon,
            $"X esperado {expected.X}, obtido {actual.X}");
        Assert.True(System.Math.Abs(expected.Y - actual.Y) < Epsilon,
            $"Y esperado {expected.Y}, obtido {actual.Y}");
    }

    [Fact]
    public void Position_FullscreenSlice_MapeiaCantosParaNdcCompleto()
    {
        var slice = new Slice { OutputRect = new RectF(0, 0, 1920, 1080) };
        var m = SliceMath.ComputePosition(slice.OutputRect, 1920, 1080);

        AssertVector2(new Vector2(-1, 1), SliceMath.TransformPoint(m, new Vector2(0, 0)));   // canto sup-esq
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 0)));    // sup-dir
        AssertVector2(new Vector2(-1, -1), SliceMath.TransformPoint(m, new Vector2(0, 1)));  // inf-esq
        AssertVector2(new Vector2(1, -1), SliceMath.TransformPoint(m, new Vector2(1, 1)));   // inf-dir
    }

    [Fact]
    public void Position_OutputRectParcial_PosicionaDentroDoMonitor()
    {
        // Metade direita de um monitor 1920x1080: X de 960 a 1920.
        var m = SliceMath.ComputePosition(new RectF(960, 0, 960, 1080), 1920, 1080);

        AssertVector2(new Vector2(0, 1), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 0)));
    }

    [Fact]
    public void Uv_R0_SemFlip_MapeiaIdentidadeDentroDoInputRect()
    {
        var slice = new Slice
        {
            InputRect = new RectF(0, 0, 1920, 1080),
            Rotation = Rotation.R0,
        };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 1)));
        AssertVector2(new Vector2(0.5f, 0.5f), SliceMath.TransformPoint(m, new Vector2(0.5f, 0.5f)));
    }

    [Fact]
    public void Uv_InputRectParcial_NormalizaDentroDaComposicao()
    {
        // InputRect ocupa a metade direita da composição 1920x1080.
        var slice = new Slice { InputRect = new RectF(960, 0, 960, 1080) };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        // local(0,0) do slice deve cair no meio da composição (u=0.5), não no início.
        AssertVector2(new Vector2(0.5f, 0), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 1)));
    }

    [Fact]
    public void Uv_Rotacao90_TrocaEixosEAspecto()
    {
        // Monitor em pé: OutputRect é alto/estreito, InputRect largo — R90 deve
        // mapear o eixo Y do quad local para o eixo X (largura) da composição.
        var slice = new Slice
        {
            InputRect = new RectF(0, 0, 1920, 1080),
            Rotation = Rotation.R90,
        };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        // local (0,0) -> u=ly=0, v=1-lx=1 => (0,1)
        AssertVector2(new Vector2(0, 1), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        // local (1,0) -> u=ly=0, v=1-lx=0 => (0,0)
        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(1, 0)));
        // local (0,1) -> u=ly=1, v=1-lx=1 => (1,1)
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(0, 1)));
        // local (1,1) -> u=ly=1, v=1-lx=0 => (1,0)
        AssertVector2(new Vector2(1, 0), SliceMath.TransformPoint(m, new Vector2(1, 1)));
    }

    [Fact]
    public void Uv_Rotacao180_InverteAmbosOsEixos()
    {
        var slice = new Slice { InputRect = new RectF(0, 0, 1920, 1080), Rotation = Rotation.R180 };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(1, 1)));
    }

    [Fact]
    public void Uv_Rotacao270_TrocaEixosNaDirecaoOposta()
    {
        var slice = new Slice { InputRect = new RectF(0, 0, 1920, 1080), Rotation = Rotation.R270 };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        // local (0,0) -> u=1-ly=1, v=lx=0 => (1,0)
        AssertVector2(new Vector2(1, 0), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        // local (1,0) -> u=1-ly=1, v=lx=1 => (1,1)
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 0)));
        // local (0,1) -> u=1-ly=0, v=lx=0 => (0,0)
        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(0, 1)));
    }

    [Fact]
    public void Uv_FlipH_InverteEixoX()
    {
        var slice = new Slice { InputRect = new RectF(0, 0, 1920, 1080), FlipH = true };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        AssertVector2(new Vector2(1, 0), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(1, 0)));
        AssertVector2(new Vector2(0.5f, 0.5f), SliceMath.TransformPoint(m, new Vector2(0.5f, 0.5f)));
    }

    [Fact]
    public void Uv_FlipV_InverteEixoY()
    {
        var slice = new Slice { InputRect = new RectF(0, 0, 1920, 1080), FlipV = true };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        AssertVector2(new Vector2(0, 1), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(0, 1)));
    }

    [Fact]
    public void Uv_FlipHEV_JuntoComRotacao_ComposicaoConsistente()
    {
        // R180 + FlipH + FlipV deve equivaler a identidade (duas inversões se cancelam).
        var slice = new Slice
        {
            InputRect = new RectF(0, 0, 1920, 1080),
            Rotation = Rotation.R180,
            FlipH = true,
            FlipV = true,
        };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        AssertVector2(new Vector2(0, 0), SliceMath.TransformPoint(m, new Vector2(0, 0)));
        AssertVector2(new Vector2(1, 1), SliceMath.TransformPoint(m, new Vector2(1, 1)));
    }

    [Fact]
    public void Uv_InputRectForaDaComposicao_ProduzUvForaDe01()
    {
        // InputRect começa antes do início da composição (bezel negativo/erro de config) —
        // o UV resultante deve sair de [0,1] para o sampler (Border) descartar, não esticar.
        var slice = new Slice { InputRect = new RectF(-100, 0, 1920, 1080) };
        var m = SliceMath.ComputeUv(slice, 1920, 1080);

        var uvAtOrigin = SliceMath.TransformPoint(m, new Vector2(0, 0));
        Assert.True(uvAtOrigin.X < 0, $"esperava U negativo, obtido {uvAtOrigin.X}");
    }
}
