using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using RCN1Bridge.Core.Diagnostics;
using RCN1Bridge.Core.Input;
using RCN1Bridge.Core.Mapping;

namespace RCN1Bridge.App.Services;

public enum AppTheme { System, Light, Dark }

public enum OutputTarget
{
    XboxController,
    // 4.1 saved it under its old name
    [JsonStringEnumMemberName("droneMod")] ModsOnly,
    Both,
}

public static class OutputTargetExtensions
{
    public static bool UsesPad(this OutputTarget target) => target != OutputTarget.ModsOnly;
    public static bool UsesGameLink(this OutputTarget target) => target != OutputTarget.XboxController;
}

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RCN1Bridge");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    private DispatcherTimer? _saveTimer;

    public bool KeepInTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public AppTheme Theme { get; set; } = AppTheme.System;
    // Both acts as the Xbox controller until a mod asks for the RC, so it's the safe default
    public OutputTarget Output { get; set; } = OutputTarget.Both;

    // Off means the engine skips mapping entirely and sends the sticks straight through
    public bool MappingEnabled { get; set; }
    public MappingProfile Mapping { get; set; } = MappingProfile.Default;
    public bool TuningEnabled { get; set; }
    public TuningProfile Tuning { get; set; } = TuningProfile.Default;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
                settings.Mapping ??= MappingProfile.Default;
                settings.Tuning ??= TuningProfile.Default;
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn($"Settings unreadable, using defaults: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        _saveTimer?.Stop();
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't save settings: {ex.Message}");
        }
    }

    // Sliders change on every pixel of a drag
    public void SaveSoon()
    {
        if (_saveTimer is null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _saveTimer.Tick += (_, _) => Save();
        }
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void Flush()
    {
        if (_saveTimer?.IsEnabled == true)
            Save();
    }
}
