using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoClicker.Input;
using Avalonia.Input;

namespace AutoClicker.Core;

/// <summary>A key (or key combination) that starts and stops a saved setup from any window.</summary>
public sealed class KeyBinding
{
    public string Name { get; set; } = "";
    public Key Key { get; set; } = Key.None;
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    /// <summary>
    /// When on, pressing the key starts this run even while others are going. When off, it
    /// stops every other run first.
    /// </summary>
    public bool RunAlongside { get; set; }

    public RunSetup Setup { get; set; } = new();

    [JsonIgnore] public string KeyName => KeyMap.ComboName(Key, Modifiers);

    public bool SameKeyAs(Key key, Modifiers modifiers) => Key == key && Modifiers == modifiers;
}

/// <summary>The saved key bindings, kept as JSON next to the settings file.</summary>
public static class KeyBindingStore
{
    /// <summary>Bumped when the on-disk shape changes; a newer file is left alone rather than misread.</summary>
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoClicker", "bindings.json");

    sealed class FileShape
    {
        public int Version { get; set; } = CurrentVersion;
        public List<KeyBinding> Bindings { get; set; } = new();
    }

    public static List<KeyBinding> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(FilePath), JsonOptions);
            if (file == null || file.Version > CurrentVersion) return new();
            file.Bindings.RemoveAll(b => !KeyMap.IsSupported(b.Key));
            return file.Bindings;
        }
        catch (Exception)
        {
            return new();   // unreadable or a different shape: start with no bindings
        }
    }

    public static void Save(List<KeyBinding> bindings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new FileShape { Bindings = bindings }, JsonOptions));
        }
        catch (Exception) { /* like settings, bindings are a convenience; ignore write failures */ }
    }
}
