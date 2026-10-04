using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace RCN1Bridge.App.Services;

public static class Shell
{
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Start("explorer.exe", $"\"{path}\"");
    }

    public static void OpenUrl(string url) => Start(url, null);

    private static void Start(string target, string? args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, args ?? "") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "RCN1Bridge";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null)
            return;
        if (enabled && Environment.ProcessPath is { } exe)
            key.SetValue(Name, $"\"{exe}\" --tray");
        else
            key.DeleteValue(Name, throwOnMissingValue: false);
    }
}
