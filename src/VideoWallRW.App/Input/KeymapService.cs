using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace VideoWallRW.App.Input;

/// <summary>Carrega atalhos editáveis pelo usuário e compara-os no PreviewKeyDown central.</summary>
public sealed class KeymapService
{
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW", "keymap.json");
    public static string LayerTargetsFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW", "keymap-layers.json");

    private readonly Dictionary<string, KeyGesture> _gestures = [];
    private Dictionary<string, int?> _layerTargets = [];

    public KeymapService() => Reload();

    /// <summary>Recarrega do disco — chamado depois que o editor de atalhos (T7.7) salva mudanças.</summary>
    public void Reload()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var configured = LoadOrCreate(FilePath);
        _layerTargets = LoadLayerTargets();
        var converter = new KeyGestureConverter();

        _gestures.Clear();
        foreach (var (action, gestureText) in configured)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(gestureText) &&
                    converter.ConvertFromInvariantString(gestureText) is KeyGesture gesture)
                    _gestures[action] = gesture;
            }
            catch (NotSupportedException)
            {
                // Um atalho inválido não impede a inicialização da aplicação.
            }
        }
    }

    public bool TryGetAction(KeyEventArgs eventArgs, out string action, out int? layerIndex)
    {
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        foreach (var (candidateAction, gesture) in _gestures)
        {
            if (gesture.Key == key && gesture.Modifiers == Keyboard.Modifiers)
            {
                action = candidateAction;
                layerIndex = _layerTargets.GetValueOrDefault(candidateAction);
                return true;
            }
        }

        action = string.Empty;
        layerIndex = null;
        return false;
    }

    public static Dictionary<string, int?> LoadLayerTargets()
    {
        try
        {
            return File.Exists(LayerTargetsFilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, int?>>(File.ReadAllText(LayerTargetsFilePath)) ?? []
                : [];
        }
        catch (JsonException) { return []; }
    }

    public static void SaveLayerTargets(Dictionary<string, int?> targets)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LayerTargetsFilePath)!);
        File.WriteAllText(LayerTargetsFilePath,
            JsonSerializer.Serialize(targets, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static Dictionary<string, string> LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? Defaults();
                // Uma instalação já existente não ganha ação nova nenhuma adicionada em
                // versões futuras (ex.: NextScene/PreviousScene) só porque o arquivo já
                // existe no disco — preenche o que falta com o padrão, sem sobrescrever
                // atalhos que o usuário já customizou, e regrava se algo foi preenchido.
                bool changed = false;
                foreach (var (action, gesture) in Defaults())
                {
                    if (!loaded.ContainsKey(action)) { loaded[action] = gesture; changed = true; }
                }
                if (changed) Save(path, loaded);
                return loaded;
            }
            catch (JsonException)
            {
                return Defaults();
            }
        }

        var defaults = Defaults();
        Save(path, defaults);
        return defaults;
    }

    public static void Save(string path, Dictionary<string, string> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Quantas colunas ganham atalho por padrão quando não se sabe o tamanho
    /// do projeto (a grade nova nasce com 8).</summary>
    public const int DefaultColumns = 8;

    public static Dictionary<string, string> Defaults(int columnCount = DefaultColumns)
    {
        var defaults = new Dictionary<string, string>
        {
            ["ToggleMasterPause"] = "Ctrl+Space",
            ["ClearLayer"] = "X",
            ["ClearAll"] = "Ctrl+X",
            ["Blackout"] = "B",
            ["ToggleOutputs"] = "F",
            ["SaveProject"] = "Ctrl+S",
            ["OpenProject"] = "Ctrl+O",
            ["NewProject"] = "Ctrl+N",
            ["NextScene"] = "PageDown",
            ["PreviousScene"] = "PageUp",
        };

        for (int column = 1; column <= Math.Max(columnCount, DefaultColumns); column++)
            if (DefaultColumnGesture(column) is { } gesture)
                defaults[$"TriggerColumn{column}"] = gesture;

        return defaults;
    }

    /// <summary>
    /// Atalho padrão da coluna N (1-based): 1-9 → D1..D9, 10 → D0, 11-19 → Shift+D1..D9,
    /// 20 → Shift+D0. Acima de 20 não há padrão (o operador define manualmente no editor
    /// de atalhos) — o teclado numérico acaba.
    /// </summary>
    public static string? DefaultColumnGesture(int column)
    {
        if (column <= 0) return null;
        if (column <= 9) return $"D{column}";
        if (column == 10) return "D0";
        if (column <= 19) return $"Shift+D{column - 10}";
        if (column == 20) return "Shift+D0";
        return null;
    }

    /// <summary>
    /// Garante que existe entrada de atalho pra cada coluna do projeto (T: "atalhos
    /// acompanham a quantidade máxima de colunas do projeto"). Chamado quando a grade
    /// muda de tamanho — só ACRESCENTA o que falta, nunca sobrescreve o que o operador
    /// já personalizou, e nunca remove atalhos de colunas que sumiram (o projeto pode
    /// voltar a crescer, e cena nenhuma perde o atalho por isso).
    /// </summary>
    public static void EnsureColumns(string path, int columnCount)
    {
        try
        {
            var map = LoadOrCreate(path);
            bool changed = false;
            for (int column = 1; column <= columnCount; column++)
            {
                string action = $"TriggerColumn{column}";
                if (map.ContainsKey(action)) continue;
                if (DefaultColumnGesture(column) is not { } gesture) continue;
                // Não rouba um atalho que já pertence a outra ação.
                if (map.ContainsValue(gesture)) continue;
                map[action] = gesture;
                changed = true;
            }
            if (changed) Save(path, map);
        }
        catch { /* atalho é conveniência: nunca derruba a operação */ }
    }
}
