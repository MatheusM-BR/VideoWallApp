using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using VideoWallRW.App.ViewModels;
using VideoWallRW.App.Controls;

namespace VideoWallRW.App;

public partial class ImageEditorWindow
{
    // ── Tela: campos + guias de arrastar (direita, inferior, canto) ─────────────

    private bool _updatingTela;

    /// <summary>Em porcentagem, os campos são relativos a este tamanho (o que a tela
    /// tinha quando o modo foi ligado / no último Aplicar).</summary>
    private int _pctBaseW = 1, _pctBaseH = 1;
    private bool PercentMode => TelaUnit is not null && TelaUnit.SelectedIndex == 1;

    /// <summary>
    /// "Aplicar tamanho" acende (azul) quando há um tamanho pendente — arrastou uma
    /// guia ou digitou nos campos — e volta ao cinza depois de aplicar/redefinir. Antes
    /// ele ficava sempre igual e o operador não sabia se a mudança já tinha valido.
    /// </summary>
    private void UpdateApplyCanvasState()
    {
        if (ApplyCanvasButton is null) return;
        bool pending = false;
        if (_liveCanvasSize is { } live &&
            ((int)Math.Round(live.X) != _vm.Bitmap.Width || (int)Math.Round(live.Y) != _vm.Bitmap.Height))
            pending = true;
        else if (TryReadTelaFields(out int w, out int h) && (w != _vm.Bitmap.Width || h != _vm.Bitmap.Height))
            pending = true;
        ApplyCanvasButton.Tag = pending ? "pending" : null;
    }

    private bool TryReadTelaFields(out int w, out int h)
    {
        w = h = 0;
        if (TelaW is null || TelaH is null) return false;
        if (!double.TryParse(TelaW.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) || a <= 0) return false;
        if (!double.TryParse(TelaH.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double b) || b <= 0) return false;
        w = Math.Max(PercentMode ? (int)Math.Round(_pctBaseW * a / 100.0) : (int)Math.Round(a), 1);
        h = Math.Max(PercentMode ? (int)Math.Round(_pctBaseH * b / 100.0) : (int)Math.Round(b), 1);
        return true;
    }

    private void UpdateTelaFields()
    {
        _updatingTela = true;
        _pctBaseW = Math.Max(_vm.Bitmap.Width, 1);
        _pctBaseH = Math.Max(_vm.Bitmap.Height, 1);
        if (PercentMode)
        {
            TelaW.Text = "100";
            TelaH.Text = "100";
        }
        else
        {
            TelaW.Text = _vm.Bitmap.Width.ToString(CultureInfo.InvariantCulture);
            TelaH.Text = _vm.Bitmap.Height.ToString(CultureInfo.InvariantCulture);
        }
        _updatingTela = false;
        UpdateApplyCanvasState();
    }

    private void OnTelaUnitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateTelaFields();   // troca de unidade só reescreve os campos, não redimensiona
    }

    /// <summary>
    /// Digitar apenas ESPELHA o outro campo quando a proporção está travada — NÃO
    /// aplica. Antes isto redimensionava a cada tecla: digitar "1000" destruía a arte
    /// em 1px, depois 10px, depois 100px, empilhando 4 níveis de desfazer. Agora o
    /// redimensionamento acontece só no ENTER ou ao sair do campo, como no Paint 3D.
    /// </summary>
    private void OnTelaFieldChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingTela) return;
        try { MirrorTelaField(sender); }
        finally { UpdateApplyCanvasState(); }
    }

    private void MirrorTelaField(object sender)
    {
        if (!_vm.LockAspect) return;

        double aspect = _vm.Bitmap.Height > 0 ? (double)_vm.Bitmap.Width / _vm.Bitmap.Height : 1;
        _updatingTela = true;
        if (ReferenceEquals(sender, TelaW) && double.TryParse(TelaW.Text, out double w) && w > 0)
            TelaH.Text = PercentMode
                ? TelaW.Text                                        // mesma % nos dois eixos
                : Math.Max((int)Math.Round(w / aspect), 1).ToString(CultureInfo.InvariantCulture);
        else if (ReferenceEquals(sender, TelaH) && double.TryParse(TelaH.Text, out double h) && h > 0)
            TelaW.Text = PercentMode
                ? TelaH.Text
                : Math.Max((int)Math.Round(h * aspect), 1).ToString(CultureInfo.InvariantCulture);
        _updatingTela = false;
    }

    private void OnTelaKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplyTelaFromFields();
        e.Handled = true;
    }

    private void OnTelaCommit(object sender, RoutedEventArgs e) => ApplyTelaFromFields();

    private void OnApplyCanvas(object sender, RoutedEventArgs e) => ApplyTelaFromFields();

    private void OnResetCanvas(object sender, RoutedEventArgs e)
    {
        _liveCanvasSize = new SKPoint(_vm.Bitmap.Width, _vm.Bitmap.Height);
        UpdateTelaFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>Lê os campos (em px ou %) e aplica de uma vez só.</summary>
    private void ApplyTelaFromFields()
    {
        if (TryReadTelaFields(out int w, out int h))
            ApplyCanvasSize(w, h);
    }

    private void ApplyCanvasSize(int w, int h)
    {
        _liveCanvasSize = null;
        if (w <= 0 || h <= 0 || (w == _vm.Bitmap.Width && h == _vm.Bitmap.Height))
        {
            UpdateTelaFields(); // nada a aplicar: apaga o botão
            return;
        }
        if (_vm.ResizeImageWithCanvas)
            _vm.ApplyResize(w, h);
        else
            _vm.ApplyCanvasResize(w, h);
        UpdateTelaFields();
        Canvas.InvalidateVisual();
    }

    /// <summary>
    /// 8 alças (estilo Paint/Resolume): direita/esquerda crescem ou encolhem a tela
    /// pros dois lados, cima/baixo idem no eixo vertical, cantos combinam os dois.
    /// Como o resultado final sempre CENTRALIZA a imagem original (ver
    /// <see cref="ImageEditorViewModel.ApplyCanvasResize"/>), não importa de que lado
    /// você arrasta — o efeito prático é "abrir mais tela" e a arte fica no meio.
    /// </summary>
    private void BeginCanvasDrag(SKPoint pt)
    {
        _liveCanvasSize ??= new SKPoint(_vm.Bitmap.Width, _vm.Bitmap.Height);
        _canvasDrag = HitCanvasHandle(pt);
        if (_canvasDrag != CanvasHandle.None)
        {
            var (w, h) = SceneSize();
            _fitFreeze = new SKPoint(w, h);
        }
    }

    /// <summary>Retângulo do TRACEJADO em coordenadas da imagem: a tela nova
    /// centralizada na imagem atual (mesma conta do desenho e do Aplicar).</summary>
    private SKRect LiveCanvasRect()
    {
        var bmp = _vm.Bitmap;
        var size = _liveCanvasSize ?? new SKPoint(bmp.Width, bmp.Height);
        float left = (bmp.Width - size.X) / 2f, top = (bmp.Height - size.Y) / 2f;
        return new SKRect(left, top, left + size.X, top + size.Y);
    }

    private CanvasHandle HitCanvasHandle(SKPoint pt)
    {
        // As alças são as do tracejado (tamanho em prévia), não as da imagem atual.
        var r = LiveCanvasRect();
        float tolerance = (float)(16 / Math.Max(CanvasScale(), 0.01));
        bool nearLeft = Math.Abs(pt.X - r.Left) <= tolerance;
        bool nearRight = Math.Abs(pt.X - r.Right) <= tolerance;
        bool nearTop = Math.Abs(pt.Y - r.Top) <= tolerance;
        bool nearBottom = Math.Abs(pt.Y - r.Bottom) <= tolerance;
        bool midY = pt.Y > r.Top - tolerance && pt.Y < r.Bottom + tolerance;
        bool midX = pt.X > r.Left - tolerance && pt.X < r.Right + tolerance;

        return (nearLeft, nearRight, nearTop, nearBottom) switch
        {
            (true, false, true, false) => CanvasHandle.NW,
            (false, true, true, false) => CanvasHandle.NE,
            (true, false, false, true) => CanvasHandle.SW,
            (false, true, false, true) => CanvasHandle.SE,
            (true, false, false, false) when midY => CanvasHandle.W,
            (false, true, false, false) when midY => CanvasHandle.E,
            (false, false, true, false) when midX => CanvasHandle.N,
            (false, false, false, true) when midX => CanvasHandle.S,
            _ => CanvasHandle.None,
        };
    }

    private void ContinueCanvasDrag(SKPoint pt)
    {
        var bmp = _vm.Bitmap;
        var live = _liveCanvasSize ?? new SKPoint(bmp.Width, bmp.Height);
        float w = live.X, h = live.Y;
        double aspect = bmp.Height > 0 ? (double)bmp.Width / bmp.Height : 1;

        // A tela cresce igual pros DOIS lados (a imagem fica no meio), então a alça
        // fica exatamente sob o mouse quando a largura = 2 × distância ao centro.
        float cx = bmp.Width / 2f, cy = bmp.Height / 2f;
        float wFromRight() => Math.Max(2 * (pt.X - cx), 8);
        float wFromLeft() => Math.Max(2 * (cx - pt.X), 8);
        float hFromBottom() => Math.Max(2 * (pt.Y - cy), 8);
        float hFromTop() => Math.Max(2 * (cy - pt.Y), 8);

        switch (_canvasDrag)
        {
            case CanvasHandle.E: w = wFromRight(); if (_vm.LockAspect) h = (float)(w / aspect); break;
            case CanvasHandle.W: w = wFromLeft(); if (_vm.LockAspect) h = (float)(w / aspect); break;
            case CanvasHandle.S: h = hFromBottom(); if (_vm.LockAspect) w = (float)(h * aspect); break;
            case CanvasHandle.N: h = hFromTop(); if (_vm.LockAspect) w = (float)(h * aspect); break;
            case CanvasHandle.SE: w = wFromRight(); h = _vm.LockAspect ? (float)(w / aspect) : hFromBottom(); break;
            case CanvasHandle.SW: w = wFromLeft(); h = _vm.LockAspect ? (float)(w / aspect) : hFromBottom(); break;
            case CanvasHandle.NE: w = wFromRight(); h = _vm.LockAspect ? (float)(w / aspect) : hFromTop(); break;
            case CanvasHandle.NW: w = wFromLeft(); h = _vm.LockAspect ? (float)(w / aspect) : hFromTop(); break;
        }

        _liveCanvasSize = new SKPoint(w, h);
        _updatingTela = true;
        TelaW.Text = ((int)w).ToString(CultureInfo.InvariantCulture);
        TelaH.Text = ((int)h).ToString(CultureInfo.InvariantCulture);
        _updatingTela = false;
        UpdateApplyCanvasState();
    }

}
