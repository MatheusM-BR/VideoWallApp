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

    private readonly Dictionary<string, KeyGesture> _gestures = [];

    public KeymapService() => Reload();

    /// <summary>Recarrega do disco — chamado depois que o editor de atalhos (T7.7) salva mudanças.</summary>
    public void Reload()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var configured = LoadOrCreate(FilePath);
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

    public bool TryGetAction(KeyEventArgs eventArgs, out string action)
    {
        Key key = eventArgs.Key == Key.System ? eventArgs.SystemKey : eventArgs.Key;
        foreach (var (candidateAction, gesture) in _gestures)
        {
            if (gesture.Key == key && gesture.Modifiers == Keyboard.Modifiers)
            {
                action = candidateAction;
                return true;
            }
        }

        action = string.Empty;
        return false;
    }

    public static Dictionary<string, string> LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? Defaults();
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

    public static Dictionary<string, string> Defaults()
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
        };

        for (int column = 1; column <= 8; column++)
            defaults[$"TriggerColumn{column}"] = $"D{column}";

        return defaults;
    }
}
