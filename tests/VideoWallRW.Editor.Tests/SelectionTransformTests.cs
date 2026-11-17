using SkiaSharp;
using VideoWallRW.App.ViewModels;
using Xunit;

namespace VideoWallRW.Editor.Tests;

/// <summary>
/// Espelhar, girar (90° e livre) e copiar/recortar/colar da Seleção — conferindo pixels.
/// Mesma imagem de teste: 200×100, esquerda vermelha, direita azul, quadrado verde
/// 20×20 em (40,40)-(60,60).
/// </summary>
public sealed class SelectionTransformTests : IDisposable
{
    private static readonly SKColor Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 255, 0), White = SKColors.White;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwvw-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _image;

    public SelectionTransformTests()
    {
        Directory.CreateDirectory(_dir);
        _image = Path.Combine(_dir, "teste.png");
        using var bmp = new SKBitmap(200, 100);
        using (var c = new SKCanvas(bmp))
        using (var p = new SKPaint())
        {
            p.Color = Red; c.DrawRect(0, 0, 100, 100, p);
            p.Color = Blue; c.DrawRect(100, 0, 100, 100, p);
            p.Color = Green; c.DrawRect(40, 40, 20, 20, p);
        }
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(_image, data.ToArray());
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private ImageEditorViewModel Open() => new(_image) { Tool = EditorTool.Select };

    private static void AssertColor(SKColor expected, SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Assert.True(Math.Abs(c.Red - expected.Red) <= 10 && Math.Abs(c.Green - expected.Green) <= 10 && Math.Abs(c.Blue - expected.Blue) <= 10,
            $"Pixel ({x},{y}) = {c}, esperado {expected}");
    }

    [Fact]
    public void Espelhar_na_horizontal_inverte_esquerda_e_direita()
    {
        using var vm = Open();
        vm.SetSelection(SKRect.Create(0, 0, 200, 100));
        vm.FlipSelection(horizontal: true);
        vm.CommitSelection();
        AssertColor(Blue, vm.Bitmap, 10, 10);
        AssertColor(Red, vm.Bitmap, 190, 10);
        AssertColor(Green, vm.Bitmap, 150, 50); // quadrado foi pra 140-160
        AssertColor(Blue, vm.Bitmap, 50, 50);
    }

    [Fact]
    public void Espelhar_na_vertical_so_dentro_da_area()
    {
        using var vm = Open();
        vm.SetSelection(SKRect.Create(40, 40, 20, 40));   // área 40-80: verde em cima (40-60), vermelho embaixo
        vm.FlipSelection(horizontal: false);
        vm.CommitSelection();
        AssertColor(Red, vm.Bitmap, 50, 45);
        AssertColor(Green, vm.Bitmap, 50, 75);
        AssertColor(Red, vm.Bitmap, 30, 50); // fora da área: intacto
    }

    [Fact]
    public void Girar_90_troca_largura_e_altura_em_torno_do_centro()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(30, 40, 70, 60));   // vermelho|verde|vermelho, 40×20
        vm.RotateSelection90(clockwise: true);
        Assert.Equal(new SKRect(40, 30, 60, 70), vm.Selection); // 20×40, mesmo centro
        vm.CommitSelection();
        AssertColor(Red, vm.Bitmap, 50, 33);
        AssertColor(Green, vm.Bitmap, 50, 50);
        AssertColor(Red, vm.Bitmap, 50, 67);
        AssertColor(White, vm.Bitmap, 34, 50); // parte da área original que ficou descoberta
    }

    [Fact]
    public void Girar_90_quatro_vezes_volta_ao_original()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(30, 40, 70, 60));
        for (int i = 0; i < 4; i++) vm.RotateSelection90(clockwise: true);
        Assert.Equal(new SKRect(30, 40, 70, 60), vm.Selection);
        vm.CommitSelection();
        AssertColor(Red, vm.Bitmap, 33, 50);
        AssertColor(Green, vm.Bitmap, 50, 50);
    }

    [Fact]
    public void Giro_livre_de_45_graus_vira_losango()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.SetSelectionAngle(45);
        Assert.Equal(45, vm.SelectionAngle);
        vm.CommitSelection();
        AssertColor(Green, vm.Bitmap, 50, 50);
        AssertColor(Green, vm.Bitmap, 50, 42);  // ponta de cima do losango
        AssertColor(White, vm.Bitmap, 42, 42);  // canto antigo: virou buraco
        // A seleção passa a ser o contorno do que foi gravado (~28×28).
        Assert.InRange(vm.Selection!.Value.Width, 27.5f, 29f);
    }

    [Fact]
    public void Angulo_normaliza_entre_menos_180_e_180()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.SetSelectionAngle(270);
        Assert.Equal(-90, vm.SelectionAngle);
        vm.SetSelectionAngle(-540);
        Assert.Equal(180, vm.SelectionAngle);
    }

    [Fact]
    public void Espelhar_com_giro_inverte_o_giro()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.SetSelectionAngle(30);
        vm.FlipSelection(horizontal: true);
        Assert.Equal(-30, vm.SelectionAngle);
    }

    [Fact]
    public void Cancelar_desfaz_espelho_e_giro()
    {
        using var vm = Open();
        vm.SetSelection(SKRect.Create(0, 0, 200, 100));
        vm.FlipSelection(true);
        vm.SetSelectionAngle(20);
        vm.CancelSelection();
        AssertColor(Red, vm.Bitmap, 10, 10);
        AssertColor(Green, vm.Bitmap, 50, 50);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void Copiar_e_colar_duplica_a_area()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        var copy = vm.CopySelection();
        Assert.NotNull(copy);
        Assert.Equal(20, copy!.Width);
        AssertColor(Green, vm.Bitmap, 50, 50); // copiar não altera nada
        Assert.False(vm.CanUndo);

        vm.SetSelection(null);
        Assert.True(vm.PasteImage());
        Assert.True(vm.IsSelectionFloating);
        Assert.Equal(SKRect.Create(90, 40, 20, 20), vm.Selection); // sem seleção: cola no centro
        vm.SetFloatingRect(SKRect.Create(140, 40, 20, 20));
        vm.CommitSelection();
        AssertColor(Green, vm.Bitmap, 150, 50);
        AssertColor(Green, vm.Bitmap, 50, 50); // original continua
        AssertColor(Blue, vm.Bitmap, 120, 50);
    }

    [Fact]
    public void Colar_vai_para_onde_esta_a_selecao()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.CopySelection();
        vm.SetSelection(new SKRect(150, 10, 170, 30));
        vm.PasteImage();
        Assert.Equal(SKRect.Create(150, 10, 20, 20), vm.Selection);
    }

    [Fact]
    public void Recortar_apaga_e_colar_traz_de_volta()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        Assert.NotNull(vm.CutSelection());
        AssertColor(White, vm.Bitmap, 50, 50);
        vm.PasteImage();
        vm.SetFloatingRect(SKRect.Create(150, 70, 20, 20));
        vm.CommitSelection();
        AssertColor(Green, vm.Bitmap, 160, 80);
        AssertColor(White, vm.Bitmap, 50, 50);
    }

    [Fact]
    public void Cancelar_a_colagem_nao_deixa_rastro()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.CopySelection();
        vm.SetSelection(null);
        vm.PasteImage();
        vm.CancelSelection();
        AssertColor(Blue, vm.Bitmap, 100, 50);
        Assert.False(vm.CanUndo);
    }

    [Fact]
    public void Colar_imagem_maior_que_a_tela_encolhe_para_caber()
    {
        using var vm = Open();
        using var big = new SKBitmap(800, 200);
        using (var c = new SKCanvas(big)) c.Clear(new SKColor(255, 255, 0));
        vm.PasteImage(big);
        Assert.Equal(SKRect.Create(0, 25, 200, 50), vm.Selection); // 4:1 cabe em 200×50
        vm.CommitSelection();
        AssertColor(new SKColor(255, 255, 0), vm.Bitmap, 100, 50);
        AssertColor(Red, vm.Bitmap, 10, 10);
    }

    [Fact]
    public void Copiar_a_area_esticada_copia_no_tamanho_esticado()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(40, 40, 100, 60));
        var copy = vm.CopySelection()!;
        Assert.Equal(60, copy.Width);
        Assert.Equal(20, copy.Height);
        AssertColor(Green, copy, 55, 10);
    }

    [Fact]
    public void Colar_no_modo_camadas_cria_camada_nova()
    {
        using var vm = Open();
        vm.LayerMode = true;
        vm.Tool = EditorTool.Select;
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.CopySelection();
        vm.PasteImage();
        var layer = Assert.Single(vm.Layers);
        Assert.Equal("Colado", layer.Name);
        Assert.False(vm.IsSelectionFloating);
    }

    [Fact]
    public void Desfazer_e_refazer_espelho_aplicado()
    {
        using var vm = Open();
        vm.SetSelection(SKRect.Create(0, 0, 200, 100));
        vm.FlipSelection(true);
        vm.CommitSelection();
        vm.UndoCommand.Execute(null);
        AssertColor(Red, vm.Bitmap, 10, 10);
        vm.RedoCommand.Execute(null);
        AssertColor(Blue, vm.Bitmap, 10, 10);
    }
}

public sealed class RotatedGeometryTests
{
    [Fact]
    public void Alca_lateral_com_area_girada_estica_na_direcao_da_area_e_segura_o_lado_oposto()
    {
        // Área 20×20 girada 90°: a alça "E" aponta pra baixo. Puxar 20 px pra baixo
        // deixa a área 40 de comprimento no eixo dela, com o lado de cima parado em y=40.
        var r = SelectionGeometry.TransformRotated(new SKRect(40, 40, 60, 60), "E", 0, 20, keepRatio: false, angle: 90);
        Assert.Equal(40, r.Width, 3);
        Assert.Equal(20, r.Height, 3);
        Assert.Equal(50, r.MidX, 3);
        Assert.Equal(60, r.MidY, 3);
        var drawn = ImageEditorViewModel.RotatedBounds(r, 90);
        Assert.Equal(40, drawn.Top, 3);
        Assert.Equal(80, drawn.Bottom, 3);
    }

    [Fact]
    public void Sem_giro_e_igual_ao_esticar_normal()
        => Assert.Equal(SelectionGeometry.Transform(new SKRect(0, 0, 10, 10), "SE", 5, 7, false),
                        SelectionGeometry.TransformRotated(new SKRect(0, 0, 10, 10), "SE", 5, 7, false, 0));

    [Theory]
    [InlineData(10, 0, false, 90)]
    [InlineData(0, -10, false, 0)]
    [InlineData(-10, 0, false, -90)]
    [InlineData(7, -8, true, 45)]
    public void Angulo_pela_alca_de_giro(float x, float y, bool snap, float expected)
        => Assert.Equal(expected, SelectionGeometry.AngleFromCenter(new SKPoint(0, 0), new SKPoint(x, y), snap), 2);
}
