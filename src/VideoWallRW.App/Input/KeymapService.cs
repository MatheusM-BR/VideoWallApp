using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows.Input;

namespace VideoWallRW.App.Input;

/// <summary>Carrega atalhos editáveis pelo usuário e compara-os no PreviewKeyDown central.</summary>
public sealed class KeymapService
{
    private readonly Dictionary<string, KeyGesture> _gestures = [];

    public KeymapService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoWallRW");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "keymap.json");

        var configured = LoadOrCreate(path);
        var converter = new KeyGestureConverter();
        foreach (var (action, gestureText) in configured)
        {
            try
            {
                if (converter.ConvertFromInvariantString(gestureText) is KeyGesture gesture)
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

    private static Dictionary<string, string> LoadOrCreate(string path)
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
        File.WriteAllText(path, JsonSerializer.Serialize(defaults, new JsonSerializerOptions { WriteIndented = true }));
        return defaults;
    }

    private static Dictionary<string, string> Defaults()
    {
        var defaults = new Dictionary<string, string>
        {
            ["ToggleMasterPause"] = "Ctrl+Space",
            ["ClearLayer"] = "X",
            ["ClearAll"] = "Ctrl+X",
            ["Blackout"] = "B",
            ["ToggleOutputs"] = "F"
        };

        for (int column = 1; column <= 8; column++)
            defaults[$"TriggerColumn{column}"] = $"D{column}";

        return defaults;
    }
}
