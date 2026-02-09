using System.Diagnostics;
using Microsoft.Win32;

namespace Bramki_Notify;

public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "BramkiPowiadomienia"; // value name in registry

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
        return key?.GetValue(AppName) is string s && !string.IsNullOrWhiteSpace(s);
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey);

        if (!enable)
        {
            key.DeleteValue(AppName, throwOnMissingValue: false);
            return;
        }

        var exePath = Process.GetCurrentProcess().MainModule!.FileName!;
        key.SetValue(AppName, $"\"{exePath}\" --autostart");
    }
}