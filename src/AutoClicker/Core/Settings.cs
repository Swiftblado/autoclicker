using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace AutoClicker.Core;

/// <summary>
/// Tiny key=value settings file. Lands in %APPDATA%\AutoClicker on Windows,
/// ~/Library/Application Support/AutoClicker on macOS and ~/.config/AutoClicker on Linux.
/// </summary>
public sealed class Settings
{
    /// <summary>
    /// Bumped when stored values change meaning. Version 1 (the Windows-only release) wrote
    /// Windows virtual-key codes where we now write key names, so those files are discarded
    /// rather than misread.
    /// </summary>
    const int CurrentVersion = 2;

    readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoClicker", "settings.ini");

    public static Settings Load()
    {
        var settings = new Settings();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0) settings.values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                }

                if (settings.GetInt("Version", 1) != CurrentVersion) settings.values.Clear();
            }
        }
        catch (Exception) { /* fall back to defaults */ }
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            Set("Version", CurrentVersion);
            var lines = new List<string>();
            foreach (var pair in values) lines.Add(pair.Key + "=" + pair.Value);
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception) { /* settings are a convenience; ignore write failures */ }
    }

    public void Set(string key, object? value) =>
        values[key] = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    public decimal GetDecimal(string key, decimal fallback) =>
        values.TryGetValue(key, out var raw) &&
        decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var result)
            ? result : fallback;

    public int GetInt(string key, int fallback) => (int)GetDecimal(key, fallback);

    public bool GetBool(string key, bool fallback) =>
        values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var result) ? result : fallback;

    public string GetString(string key, string fallback) =>
        values.TryGetValue(key, out var raw) ? raw : fallback;
}
