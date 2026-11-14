namespace VideoWallRW.Core.Model;

public sealed class Composition
{
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    
    public double DefaultImageDuration { get; set; } = 10.0;

    /// <summary>Master fader (0..1) — independente do blackout (Fase 7 T7.6).</summary>
    public double MasterOpacity { get; set; } = 1.0;

    /// <summary>
    /// Blackout: escurece a SAÍDA sem alterar <see cref="MasterOpacity"/> — reversível,
    /// o fader volta exatamente de onde estava ao desligar o blackout.
    /// </summary>
    public bool Blackout { get; set; }

    /// <summary>Layers de baixo (fundo) para cima. O Índice 0 é desenhado primeiro.</summary>
    public List<Layer> Layers { get; set; } = [];

    private int _columnCount = 8;

    /// <summary>
    /// Número de colunas da grade. O setter APENAS grava o valor — ele NÃO mexe mais
    /// nos arrays das layers. Antes, atribuir aqui redimensionava (Array.Resize) o
    /// Clips de todas as layers como efeito colateral, o que exigia que todo chamador
    /// soubesse a ordem certa entre a cirurgia manual no array e a atribuição — e foi
    /// exatamente isso que derrubava o app ao remover a última coluna. Para mudar o
    /// tamanho da grade de verdade, use <see cref="SetColumnCount"/>, que redimensiona
    /// as layers explicitamente.
    /// </summary>
    public int ColumnCount
    {
        get => _columnCount;
        set
        {
            if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
            _columnCount = value;
        }
    }

    /// <summary>
    /// Muda o número de colunas E ajusta o array de clips de todas as layers pro novo
    /// tamanho (crescer preenche com vazio; encolher corta do fim). Operação explícita:
    /// quem já fez a cirurgia no array (inserir/remover no meio) deve usar o setter de
    /// <see cref="ColumnCount"/>, que não mexe em nada.
    /// </summary>
    public void SetColumnCount(int value)
    {
        if (value < 1) throw new ArgumentOutOfRangeException(nameof(value));
        _columnCount = value;
        NormalizeLayers();
    }

    /// <summary>Garante que toda layer tenha exatamente <see cref="ColumnCount"/> slots —
    /// rede de segurança contra projeto antigo/caminho que tenha deixado array fora de
    /// sincronia (era a origem do IndexOutOfRange ao remover coluna).</summary>
    public void NormalizeLayers()
    {
        foreach (var layer in Layers)
        {
            if (layer.Clips.Length == _columnCount) continue;
            var arr = layer.Clips;
            Array.Resize(ref arr, _columnCount);
            layer.Clips = arr;
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
            Blackout = Blackout,
            ColumnCount = ColumnCount
        };

        foreach (var layer in Layers)
            copy.AddLayer(layer.Copy());

        return copy;
    }
}
