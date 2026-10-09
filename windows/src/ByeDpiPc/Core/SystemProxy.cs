using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ByeDpiPc.Core;

/// <summary>
/// Points the Windows (WinINet) proxy at byedpi for Proxy mode. Chrome, Edge and most
/// apps that follow the system setting honour "socks=host:port". The previous values are
/// saved to disk so they can be restored even after a crash.
/// </summary>
public static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static string BackupPath => Path.Combine(AppSettings.Directory, "proxy-backup.json");

    private sealed record Backup(int Enable, string? Server, string? Override);

    public static void Enable(string host, int port)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key);
        if (!File.Exists(BackupPath))
        {
            var backup = new Backup(
                key.GetValue("ProxyEnable") is int e ? e : 0,
                key.GetValue("ProxyServer") as string,
                key.GetValue("ProxyOverride") as string);
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(BackupPath, JsonSerializer.Serialize(backup));
        }
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", $"socks={host}:{port}", RegistryValueKind.String);
        key.SetValue("ProxyOverride", "<local>;localhost;127.*;10.*;192.168.*", RegistryValueKind.String);
        Refresh();
        Log.Write($"System proxy set to socks={host}:{port}");
    }

    /// <summary>Restores whatever was there before. Safe to call when nothing was changed.</summary>
    public static void Restore()
    {
        if (!File.Exists(BackupPath)) return;
        try
        {
            var backup = JsonSerializer.Deserialize<Backup>(File.ReadAllText(BackupPath));
            using var key = Registry.CurrentUser.CreateSubKey(Key);
            if (backup != null)
            {
                key.SetValue("ProxyEnable", backup.Enable, RegistryValueKind.DWord);
                SetOrDelete(key, "ProxyServer", backup.Server);
                SetOrDelete(key, "ProxyOverride", backup.Override);
            }
            Refresh();
            Log.Write("System proxy restored");
        }
        catch (Exception e)
        {
            Log.Write($"Could not restore the system proxy: {e.Message}");
        }
        finally
        {
            File.Delete(BackupPath);
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value != null) key.SetValue(name, value, RegistryValueKind.String);
        else key.DeleteValue(name, throwOnMissingValue: false);
    }

    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39 /* SETTINGS_CHANGED */, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /* REFRESH */, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int option, IntPtr buffer, int length);
}
