using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using SMI_Widget.Models;

namespace SMI_Widget.Services;

public static class SettingsStore
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SMI-Widget";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SMI-Widget");

    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static WidgetSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new WidgetSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<WidgetSettings>(json) ?? new WidgetSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new WidgetSettings();
        }
    }

    public static void Save(WidgetSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(FilePath, json);
    }

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value && !string.IsNullOrWhiteSpace(value);
    }

    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the Windows startup registry key.");

        if (!enabled)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            return;
        }

        var exe = Environment.ProcessPath
            ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Could not resolve the widget executable path.");
        key.SetValue(RunValueName, $"\"{exe}\"");
    }
}
