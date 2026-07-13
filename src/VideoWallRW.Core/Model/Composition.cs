namespace VideoWallRW.Core.Model;

public sealed class Composition
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    
    public double DefaultImageDuration { get; set; } = 10.0;
    public double MasterOpacity { get; set; } = 1.0;

    /// <summary>Layers de baixo (fundo) para cima. O Índice 0 é desenhado primeiro.</summary>
    public List<Layer> Layers { get; set; } = [];

    private int _columnCount = 8;
    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            _columnCount = value;
            foreach (var layer in Layers)
            {
                var arr = layer.Clips;
                Array.Resize(ref arr, value);
                layer.Clips = arr;
            }
        }
    }

    /// <summary>Adiciona uma layer garantindo que o array de clips tenha o tamanho correto.</summary>
    public void AddLayer(Layer layer)
    {
        if (layer.Clips.Length != ColumnCount)
        {
            var arr = layer.Clips;
            Array.Resize(ref arr, ColumnCount);
            layer.Clips = arr;
        }
        Layers.Add(layer);
    }

    /// <summary>
    /// Cria uma cópia independente para entrega ao engine. A UI nunca deve alterar
    /// o mesmo grafo que está sendo lido pela render thread.
    /// </summary>
    public Composition Clone()
    {
        var copy = new Composition
        {
            Width = Width,
            Height = Height,
            DefaultImageDuration = DefaultImageDuration,
            MasterOpacity = MasterOpacity,
            ColumnCount = ColumnCount
        };

        foreach (var layer in Layers)
            copy.AddLayer(layer.Copy());

        return copy;
    }
}
