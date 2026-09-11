using System.Text.Json;
using Microsoft.Win32;

namespace VivoPods.App.Services;

public sealed class UserSettings
{
    public string Theme { get; set; } = "跟随系统";
    public bool CloseToTray { get; set; } = true;
    public bool AutoReconnect { get; set; } = true;
    public bool Notifications { get; set; } = true;
    public bool Experimental { get; set; }
    public string? LastDevice { get; set; }
    public Dictionary<string, string> Profiles { get; set; } = [];
}

public sealed class SettingsStore
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VivoPodsManager");
    public UserSettings Settings { get; }
    public string? LoadError { get; }
    public SettingsStore(bool isolated = false)
    {
        if (isolated) DirectoryPath = Path.Combine(Path.GetTempPath(), "VivoPodsManager-preview");
        try
        {
            string file = Path.Combine(DirectoryPath, "settings.json");
            Settings = File.Exists(file) ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(file)) ?? new() : new();
            Settings.Profiles ??= [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        { Settings = new(); LoadError = $"无法读取设置，已使用默认值：{ex.Message}"; }
    }
    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string file = Path.Combine(DirectoryPath, "settings.json");
        string temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, file, true);
    }
    public static bool StartupEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("VivoPodsManager") != null; }
    }
    public static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enable) key.SetValue("VivoPodsManager", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("VivoPodsManager", false);
    }
}
