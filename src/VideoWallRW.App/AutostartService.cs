using Microsoft.Win32;

namespace VideoWallRW.App;

/// <summary>
/// Liga/desliga a inicialização automática do VideoWallRW com o Windows (Fase 8 T8.2),
/// via <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> — não exige elevação
/// nem instalador, funciona por usuário (o mesmo usuário que faz logon automático na
/// máquina de instalação).
/// </summary>
public static class AutostartService
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VideoWallRW";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue(ValueName) is not null;
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(KeyPath);
        if (enabled)
        {
            string exePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs()[0];
            key.SetValue(ValueName, $"\"{exePath}\" --kiosk");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
