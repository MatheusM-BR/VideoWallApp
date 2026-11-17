using SkiaSharp;
using VideoWallRW.App.ViewModels;
using Xunit;

namespace VideoWallRW.Editor.Tests;

/// <summary>Laço, varinha mágica, borda suave e giro/espelho de camadas — pixels.</summary>
public sealed class SelectionShapeAndLayerTransformTests : IDisposable
{
    private static readonly SKColor Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 255, 0), White = SKColors.White;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwvw-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _image;

    public SelectionShapeAndLayerTransformTests()
    {
        Directory.CreateDirectory(_dir);
        _image = Write("teste.png", 200, 100, c =>
        {
            using var p = new SKPaint();
            p.Color = Red; c.DrawRect(0, 0, 100, 100, p);
            p.Color = Blue; c.DrawRect(100, 0, 100, 100, p);
            p.Color = Green; c.DrawRect(40, 40, 20, 20, p);
        });
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Write(string name, int w, int h, Action<SKCanvas> draw)
    {
        string path = Path.Combine(_dir, name);
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) draw(c);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private ImageEditorViewModel Open() => new(_image) { Tool = EditorTool.Select };

    private static void AssertColor(SKColor expected, SKBitmap bmp, int x, int y, int tol = 12)
    {
        var c = bmp.GetPixel(x, y);
        Assert.True(Math.Abs(c.Red - expected.Red) <= tol && Math.Abs(c.Green - expected.Green) <= tol && Math.Abs(c.Blue - expected.Blue) <= tol,
            $"Pixel ({x},{y}) = {c}, esperado {expected}");
    }

    // ── Varinha mágica ──

    [Fact]
    public void Varinha_pega_so_a_regiao_continua_da_mesma_cor()
    {
        using var vm = Open();
        vm.SelectByColorAt(new SKPoint(50, 50));
        Assert.Equal(SKRect.Create(40, 40, 20, 20), vm.Selection);
        Assert.True(vm.HasShapedSelection);
        vm.SelectByColorAt(new SKPoint(10, 10)); // vermelho em volta do quadrado verde
        Assert.Equal(SKRect.Create(0, 0, 100, 100), vm.Selection);
    }

    [Fact]
    public void Apagar_com_a_varinha_no_vermelho_nao_toca_no_verde_de_dentro()
    {
        using var vm = Open();
        vm.SelectByColorAt(new SKPoint(10, 10));
        vm.DeleteSelection();
        AssertColor(White, vm.Bitmap, 10, 10);
        AssertColor(White, vm.Bitmap, 90, 90);
        AssertColor(Green, vm.Bitmap, 50, 50); // dentro do retângulo, mas fora da área marcada
        AssertColor(Blue, vm.Bitmap, 150, 50);
    }

    [Fact]
    public void Mover_area_da_varinha_leva_so_o_formato()
    {
        using var vm = Open();
        vm.SelectByColorAt(new SKPoint(10, 10));        // o "anel" vermelho 100×100
        vm.LiftSelection();
        vm.SetFloatingRect(SKRect.Create(100, 0, 100, 100)); // por cima do azul
        vm.CommitSelection();
        AssertColor(Red, vm.Bitmap, 110, 10);            // vermelho veio
        AssertColor(Blue, vm.Bitmap, 150, 50);           // o "furo" (onde era verde) deixa o azul aparecer
        AssertColor(Green, vm.Bitmap, 50, 50);           // verde não foi junto
        AssertColor(White, vm.Bitmap, 10, 10);
    }

    [Fact]
    public void Tolerancia_zero_pega_so_a_cor_exata()
    {
        string grad = Write("grad.png", 100, 10, c =>
        {
            using var p = new SKPaint();
            for (int x = 0; x < 100; x++) { p.Color = new SKColor((byte)(x * 2), 0, 0); c.DrawRect(x, 0, 1, 10, p); }
        });
        using var vm = new ImageEditorViewModel(grad) { Tool = EditorTool.Select, WandTolerance = 0 };
        vm.SelectByColorAt(new SKPoint(50, 5));
        Assert.Equal(1, vm.Selection!.Value.Width);
        vm.WandTolerance = 20;
        vm.SelectByColorAt(new SKPoint(50, 5));
        Assert.Equal(21, vm.Selection!.Value.Width); // ±10 colunas (2 níveis por coluna)
    }

    // ── Laço ──

    [Fact]
    public void Laco_triangular_apaga_so_dentro_do_triangulo()
    {
        using var vm = Open();
        using var path = new SKPath();
        path.MoveTo(0, 0); path.LineTo(100, 0); path.LineTo(0, 100); path.Close();
        vm.SetSelectionPath(path);
        Assert.Equal(SKRect.Create(0, 0, 100, 100), vm.Selection);
        vm.DeleteSelection();
        AssertColor(White, vm.Bitmap, 10, 10);   // dentro
        AssertColor(Red, vm.Bitmap, 90, 90);     // fora do triângulo, dentro da caixa
        AssertColor(Green, vm.Bitmap, 55, 55);   // idem
    }

    [Fact]
    public void Copiar_do_laco_deixa_o_resto_transparente()
    {
        using var vm = Open();
        using var path = new SKPath();
        path.MoveTo(0, 0); path.LineTo(100, 0); path.LineTo(0, 100); path.Close();
        vm.SetSelectionPath(path);
        var copy = vm.CopySelection()!;
        Assert.Equal(255, copy.GetPixel(10, 10).Alpha);
        Assert.Equal(0, copy.GetPixel(90, 90).Alpha);
    }

    [Fact]
    public void Laco_pequeno_demais_desmarca()
    {
        using var vm = Open();
        using var path = new SKPath();
        path.MoveTo(10, 10); path.LineTo(11, 10); path.LineTo(10, 11); path.Close();
        vm.SetSelectionPath(path);
        Assert.Null(vm.Selection);
    }

    // ── Borda suave ──

    [Fact]
    public void Borda_suave_esfuma_so_perto_da_borda()
    {
        using var vm = Open();
        vm.SelectFeather = 20;
        vm.SetSelection(SKRect.Create(0, 0, 100, 100));
        vm.DeleteSelection();
        AssertColor(White, vm.Bitmap, 50, 70);                 // meio da área: apagado
        var edge = vm.Bitmap.GetPixel(1, 50);                   // na borda: quase intacto
        Assert.True(edge.Red > 200 && edge.Green < 140, $"borda {edge}");
        var mid = vm.Bitmap.GetPixel(9, 50);                    // na faixa: mistura
        Assert.InRange(mid.Green, 40, 250);
        AssertColor(Blue, vm.Bitmap, 150, 50);
    }

    [Fact]
    public void Sem_borda_suave_o_corte_e_seco()
    {
        using var vm = Open();
        vm.SetSelection(SKRect.Create(0, 0, 100, 100));
        vm.DeleteSelection();
        AssertColor(White, vm.Bitmap, 0, 50, 2);
        AssertColor(Blue, vm.Bitmap, 100, 50, 2);
    }

    // ── Camadas giradas/espelhadas ──

    private EditorLayer AddLogo(ImageEditorViewModel vm)
    {
        // 40×20: metade esquerda verde, direita azul.
        string logo = Write("logo.png", 40, 20, c =>
        {
            using var p = new SKPaint { Color = Green };
            c.DrawRect(0, 0, 20, 20, p);
            p.Color = Blue; c.DrawRect(20, 0, 20, 20, p);
        });
        vm.LayerMode = true;
        vm.AddImageLayer(logo);
        var layer = vm.Layers.Last();
        layer.X = 30; layer.Y = 40; layer.Width = 40; layer.Height = 20; // centro (50,50)
        vm.Tool = EditorTool.Layers;
        vm.ActiveLayer = layer;
        return layer;
    }

    [Fact]
    public void Espelhar_camada_troca_os_lados_ao_mesclar()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        vm.FlipLayerCommand.Execute("h");
        Assert.True(layer.FlipX);
        vm.LayerMode = false; // mescla
        AssertColor(Blue, vm.Bitmap, 35, 50);
        AssertColor(Green, vm.Bitmap, 65, 50);
    }

    [Fact]
    public void Girar_camada_90_fica_em_pe_ao_mesclar()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        vm.RotateLayerCommand.Execute("horario");
        Assert.Equal(90, layer.Rotation);
        Assert.Equal(SKRect.Create(40, 30, 20, 40), layer.DisplayBounds);
        vm.LayerMode = false;
        AssertColor(Green, vm.Bitmap, 50, 35);  // esquerda (verde) foi pra cima
        AssertColor(Blue, vm.Bitmap, 50, 65);
        AssertColor(Red, vm.Bitmap, 35, 50);    // onde a camada deitada estava: fundo
    }

    [Fact]
    public void Clique_na_camada_girada_respeita_o_giro()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        layer.Rotation = 90;
        Assert.True(layer.Contains(new SKPoint(50, 33)));   // em pé: dentro
        Assert.False(layer.Contains(new SKPoint(33, 50)));  // onde estaria deitada: fora
    }

    [Fact]
    public void Pincel_na_camada_girada_e_espelhada_cai_no_pixel_certo()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        layer.Rotation = 90;
        layer.FlipX = true;
        // Ponto de cima da camada em pé → antes do giro era a esquerda; com o espelho, a direita.
        var local = layer.ToLocal(new SKPoint(50, 33));
        Assert.InRange(local.X, 30, 40);
        Assert.InRange(local.Y, 8, 12);
    }

    [Fact]
    public void Girar_e_espelhar_camada_desfaz_com_CtrlZ()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        vm.RotateLayerCommand.Execute("horario");
        vm.FlipLayerCommand.Execute("v");
        vm.UndoCommand.Execute(null);
        vm.UndoCommand.Execute(null);
        var restored = vm.Layers.Last();
        Assert.Equal(0, restored.Rotation);
        Assert.False(restored.FlipY);
    }

    [Fact]
    public void Zerar_giro_da_camada()
    {
        using var vm = Open();
        var layer = AddLogo(vm);
        layer.Rotation = 33; layer.FlipX = true;
        vm.ResetLayerRotationCommand.Execute(null);
        Assert.False(layer.IsTransformed);
    }

    [Fact]
    public void Texto_girado_continua_vetorial_e_gira_junto()
    {
        using var vm = Open();
        vm.LayerMode = true;
        vm.Tool = EditorTool.Text;
        var text = vm.AddTextLayer(new SKRect(20, 20, 180, 60))!;
        text.Rotation = 180;
        var clone = text.Clone();
        Assert.Equal(180, clone.Rotation);
    }
}
