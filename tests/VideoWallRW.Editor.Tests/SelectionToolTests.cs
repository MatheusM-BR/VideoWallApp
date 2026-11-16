using SkiaSharp;
using VideoWallRW.App.ViewModels;
using Xunit;

namespace VideoWallRW.Editor.Tests;

/// <summary>
/// Ferramenta Seleção do editor: marcar, esticar, mover, cancelar, apagar, desfazer —
/// conferindo PIXELS da imagem antes/depois, não só que "não deu erro".
/// Imagem de teste 200×100: metade esquerda vermelha, direita azul, e um quadrado
/// verde 20×20 em (40,40)-(60,60).
/// </summary>
public sealed class SelectionToolTests : IDisposable
{
    private static readonly SKColor Red = new(255, 0, 0), Blue = new(0, 0, 255), Green = new(0, 255, 0);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rwvw-editor-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _image;

    public SelectionToolTests()
    {
        Directory.CreateDirectory(_dir);
        _image = Path.Combine(_dir, "teste.png");
        WritePng(_image, 200, 100, c =>
        {
            using var p = new SKPaint();
            p.Color = Red; c.DrawRect(0, 0, 100, 100, p);
            p.Color = Blue; c.DrawRect(100, 0, 100, 100, p);
            p.Color = Green; c.DrawRect(40, 40, 20, 20, p);
        });
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static void WritePng(string path, int w, int h, Action<SKCanvas> draw)
    {
        using var bmp = new SKBitmap(w, h);
        using (var c = new SKCanvas(bmp)) draw(c);
        using var data = SKImage.FromBitmap(bmp).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private ImageEditorViewModel Open()
    {
        var vm = new ImageEditorViewModel(_image) { Tool = EditorTool.Select };
        return vm;
    }

    private static void AssertColor(SKColor expected, SKBitmap bmp, int x, int y)
    {
        var c = bmp.GetPixel(x, y);
        Assert.True(Math.Abs(c.Red - expected.Red) <= 8 && Math.Abs(c.Green - expected.Green) <= 8 && Math.Abs(c.Blue - expected.Blue) <= 8,
            $"Pixel ({x},{y}) = {c}, esperado {expected}");
    }

    [Fact]
    public void Abre_com_camadas_mescladas_e_a_foto_como_imagem()
    {
        using var vm = Open();
        Assert.False(vm.LayerMode);
        Assert.Empty(vm.Layers);
        AssertColor(Green, vm.Bitmap, 50, 50);
        AssertColor(Blue, vm.Bitmap, 150, 50);
    }

    [Fact]
    public void Marcar_area_nao_altera_a_imagem()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(60, 60, 40, 40)); // de trás pra frente: normaliza
        Assert.Equal(new SKRect(40, 40, 60, 60), vm.Selection);
        Assert.False(vm.IsSelectionFloating);
        Assert.False(vm.CanUndo);
        AssertColor(Green, vm.Bitmap, 50, 50);
    }

    [Fact]
    public void Selecao_fica_dentro_da_imagem()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(-50, -10, 500, 300));
        Assert.Equal(new SKRect(0, 0, 200, 100), vm.Selection);
    }

    [Fact]
    public void Esticar_na_horizontal_grava_a_area_esticada_e_deixa_o_resto_igual()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        Assert.True(vm.LiftSelection());
        AssertColor(SKColors.White, vm.Bitmap, 50, 50); // buraco com a cor de fundo enquanto flutua
        vm.SetFloatingRect(new SKRect(40, 40, 100, 60)); // 3× mais largo
        vm.CommitSelection();

        Assert.False(vm.IsSelectionFloating);
        Assert.Equal(new SKRect(40, 40, 100, 60), vm.Selection); // continua marcada no lugar novo
        foreach (int x in new[] { 42, 50, 70, 90, 97 }) AssertColor(Green, vm.Bitmap, x, 50);
        AssertColor(Red, vm.Bitmap, 50, 30);   // acima da área: intacto
        AssertColor(Red, vm.Bitmap, 30, 50);   // à esquerda: intacto
        AssertColor(Blue, vm.Bitmap, 150, 50); // longe: intacto
        Assert.True(vm.CanUndo);
    }

    [Fact]
    public void Mover_deixa_o_buraco_com_a_cor_de_fundo()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(140, 40, 160, 60));
        vm.CommitSelection();
        AssertColor(SKColors.White, vm.Bitmap, 50, 50);
        AssertColor(Green, vm.Bitmap, 150, 50);
    }

    [Fact]
    public void Cancelar_volta_exatamente_como_estava_e_nao_deixa_refazer()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(0, 0, 200, 100));
        vm.CancelSelection();

        Assert.False(vm.IsSelectionFloating);
        Assert.Null(vm.Selection);
        AssertColor(Green, vm.Bitmap, 50, 50);
        AssertColor(Red, vm.Bitmap, 10, 10);
        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
    }

    [Fact]
    public void CtrlZ_durante_a_transformacao_cancela()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(40, 40, 120, 60));
        vm.UndoCommand.Execute(null);
        Assert.False(vm.IsSelectionFloating);
        AssertColor(Green, vm.Bitmap, 50, 50);
        AssertColor(Red, vm.Bitmap, 80, 50);
    }

    [Fact]
    public void Desfazer_depois_de_aplicar_volta_a_imagem_original_e_refazer_reaplica()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(40, 40, 100, 60));
        vm.CommitSelection();

        vm.UndoCommand.Execute(null);
        AssertColor(Red, vm.Bitmap, 80, 50);
        AssertColor(Green, vm.Bitmap, 50, 50);
        vm.RedoCommand.Execute(null);
        AssertColor(Green, vm.Bitmap, 80, 50);
    }

    [Fact]
    public void Apagar_area_deixa_cor_de_fundo()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.DeleteSelection();
        Assert.Null(vm.Selection);
        AssertColor(SKColors.White, vm.Bitmap, 50, 50);
        AssertColor(Red, vm.Bitmap, 30, 50);
        vm.UndoCommand.Execute(null);
        AssertColor(Green, vm.Bitmap, 50, 50);
    }

    [Theory]
    [InlineData("tela", 0, 0, 200, 100)]
    [InlineData("largura", 0, 40, 200, 60)]
    [InlineData("altura", 40, 0, 60, 100)]
    public void Botoes_de_esticar(string modo, float l, float t, float r, float b)
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.StretchSelectionCommand.Execute(modo);
        Assert.Equal(new SKRect(l, t, r, b), vm.Selection);
        vm.CommitSelection();
        AssertColor(Green, vm.Bitmap, (int)(l + 2), (int)(t + 2));
        AssertColor(Green, vm.Bitmap, (int)(r - 3), (int)(b - 3));
    }

    [Fact]
    public void Campos_de_largura_e_altura_redimensionam_pelo_canto_superior_esquerdo()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.ResizeSelectionTo(50, 30);
        Assert.Equal(new SKRect(40, 40, 90, 70), vm.Selection);
    }

    [Fact]
    public void Salvar_com_a_area_ainda_flutuando_inclui_a_area()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(140, 40, 160, 60));
        var flat = vm.ComposeFlattened();
        AssertColor(Green, flat, 150, 50);
        AssertColor(SKColors.White, flat, 50, 50);
        Assert.True(vm.IsSelectionFloating); // compor pra salvar não encerra a edição
        if (!ReferenceEquals(flat, vm.Bitmap)) flat.Dispose();
    }

    [Fact]
    public void Trocar_de_ferramenta_aplica_e_desmarca()
    {
        using var vm = Open();
        vm.SetSelection(new SKRect(40, 40, 60, 60));
        vm.LiftSelection();
        vm.SetFloatingRect(new SKRect(40, 40, 100, 60));
        vm.Tool = EditorTool.Brush;
        Assert.False(vm.IsSelectionFloating);
        Assert.Null(vm.Selection);
        AssertColor(Green, vm.Bitmap, 90, 50);
    }

    [Fact]
    public void Selecionar_tudo_abre_a_ferramenta_e_marca_a_imagem_inteira()
    {
        using var vm = new ImageEditorViewModel(_image);
        vm.SelectAllCommand.Execute(null);
        Assert.Equal(EditorTool.Select, vm.Tool);
        Assert.Equal(new SKRect(0, 0, 200, 100), vm.Selection);
    }

    [Fact]
    public void No_modo_camadas_estica_dentro_da_camada_sem_tocar_o_fundo()
    {
        string logo = Path.Combine(_dir, "logo.png");
        WritePng(logo, 40, 40, c =>
        {
            using var p = new SKPaint { Color = Blue };
            c.DrawRect(0, 0, 40, 40, p);
            p.Color = Green; c.DrawRect(0, 0, 20, 40, p); // metade esquerda verde
        });
        using var vm = Open();
        vm.LayerMode = true;
        vm.AddImageLayer(logo);
        var layer = Assert.Single(vm.Layers);
        layer.X = 120; layer.Y = 30; layer.Width = 40; layer.Height = 40;
        vm.Tool = EditorTool.Select;

        vm.SetSelection(new SKRect(120, 30, 140, 70)); // metade verde da camada
        Assert.True(vm.LiftSelection());
        vm.SetFloatingRect(new SKRect(120, 30, 160, 70)); // estica até cobrir a camada toda
        vm.CommitSelection();

        Assert.Single(vm.Layers);
        AssertColor(Green, layer.Bitmap!, 35, 20); // lado direito da camada agora é verde
        AssertColor(Blue, vm.Bitmap, 150, 50);     // fundo por baixo continua azul
    }

    [Fact]
    public void Conta_gotas_pega_a_cor_e_passa_pro_balde()
    {
        using var vm = Open();
        vm.Tool = EditorTool.Eyedropper;
        vm.PickColorAt(new SKPoint(150, 50));
        Assert.Equal(EditorTool.Bucket, vm.Tool);
        Assert.Equal(Blue, vm.BrushColor.WithAlpha(255));
        vm.FloodFillAt(new SKPoint(10, 10)); // preenche o vermelho com o azul pego
        AssertColor(Blue, vm.Bitmap, 10, 10);
        AssertColor(Green, vm.Bitmap, 50, 50);
    }

    [Fact]
    public void Forma_se_funde_na_imagem_ao_trocar_de_ferramenta_com_camadas_mescladas()
    {
        using var vm = Open();
        vm.Tool = EditorTool.Shape;
        vm.SetColorHex("#FFFF00");
        vm.AddShapeLayer(new SKPoint(150, 10), new SKPoint(190, 30));
        Assert.Single(vm.Layers);
        vm.Tool = EditorTool.Select;
        Assert.Empty(vm.Layers);
        AssertColor(new SKColor(255, 255, 0), vm.Bitmap, 170, 20);
    }

    [Fact]
    public void Desfazer_varias_vezes_depois_de_balde_e_conta_gotas_nao_quebra()
    {
        // Regressão do fechamento do app (desfazer liberava a imagem antes da troca).
        using var vm = Open();
        for (int i = 0; i < 5; i++)
        {
            vm.Tool = EditorTool.Eyedropper;
            vm.PickColorAt(new SKPoint(i % 2 == 0 ? 150 : 10, 50));
            vm.FloodFillAt(new SKPoint(50, 50));
        }
        for (int i = 0; i < 8; i++) vm.UndoCommand.Execute(null);
        for (int i = 0; i < 3; i++) vm.RedoCommand.Execute(null);
        for (int i = 0; i < 8; i++) vm.UndoCommand.Execute(null);
        AssertColor(Green, vm.Bitmap, 50, 50);
        Assert.Equal(200, vm.Bitmap.Width);
    }
}

public sealed class SelectionGeometryTests
{
    private static readonly SKRect O = new(40, 40, 60, 60);

    [Theory]
    [InlineData("E", 40, 0, 40, 40, 100, 60)]   // lateral direita: só largura
    [InlineData("W", -10, 0, 30, 40, 60, 60)]   // lateral esquerda
    [InlineData("N", 0, -20, 40, 20, 60, 60)]   // topo: só altura
    [InlineData("S", 0, 30, 40, 40, 60, 90)]
    [InlineData("SE", 20, 10, 40, 40, 80, 70)]  // canto: os dois eixos livres
    [InlineData("Move", 5, -5, 45, 35, 65, 55)]
    [InlineData("E", -40, 0, 20, 40, 40, 60)]   // passou da borda oposta: inverte o lado, sem quebrar
    public void Alcas(string handle, float dx, float dy, float l, float t, float r, float b)
        => Assert.Equal(new SKRect(l, t, r, b), SelectionGeometry.Transform(O, handle, dx, dy, keepRatio: false));

    [Fact]
    public void Canto_com_Shift_mantem_a_proporcao()
    {
        var wide = new SKRect(0, 0, 40, 20); // 2:1
        var r = SelectionGeometry.Transform(wide, "SE", 40, 0, keepRatio: true);
        Assert.Equal(new SKRect(0, 0, 80, 40), r);
        var r2 = SelectionGeometry.Transform(wide, "NW", 0, -20, keepRatio: true); // puxou mais na vertical
        Assert.Equal(new SKRect(-40, -20, 40, 20), r2);
    }
}
