using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace LumaShell;

internal sealed class Preferences
{
    private static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Luma Shell", "settings.json");

    public bool Chrome { get; set; } = true;
    public bool AutoStart { get; set; } = true;
    public bool AutoInstallUpdates { get; set; } = true;
    public bool DockAlways { get; set; } = true;
    public bool RouteFolders { get; set; } = true;
    public bool StyleExplorerFrame { get; set; } = true;
    public bool HideWindowsTaskbar { get; set; } = true;
    public bool WinLauncher { get; set; } = true;
    public bool ReplaceSnip { get; set; } = true;
    public bool Prism { get; set; } = true;
    public string? PrismProfile { get; set; }
    public string? ScreenshotFolder { get; set; }
    public List<string> Pins { get; set; } = [];

    internal static Preferences Load()
    {
        try
        {
            var preferences = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            preferences.Pins ??= [];
            preferences.Pins.RemoveAll(path => string.IsNullOrWhiteSpace(path));
            if (string.IsNullOrWhiteSpace(preferences.ScreenshotFolder)) preferences.ScreenshotFolder = null;
            return preferences;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal bool Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, FilePath, true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
}
