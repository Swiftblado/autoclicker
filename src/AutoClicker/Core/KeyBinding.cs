using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutoClicker.Input;
using Avalonia.Input;

namespace AutoClicker.Core;

/// <summary>What pressing a binding's key does.</summary>
public enum BindingAction
{
    /// <summary>Starts and stops the binding's own saved setup.</summary>
    StartStop,
    /// <summary>Pauses the window's run, or resumes it.</summary>
    Pause,
    /// <summary>While the window's macro is paused, runs one line of it.</summary>
    Step,
    /// <summary>Makes the window's macro carry on from <see cref="KeyBinding.Line"/>, starting it if it isn't running.</summary>
    GoToLine
}

/// <summary>
/// A key (or key combination) that works from any window: it starts and stops a saved setup, or
/// pauses, steps or moves the macro running from the window.
/// </summary>
public sealed class KeyBinding
{
    public string Name { get; set; } = "";
    public Key Key { get; set; } = Key.None;
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    public BindingAction Action { get; set; } = BindingAction.StartStop;

    /// <summary>For <see cref="BindingAction.GoToLine"/>: the line, counted from 1.</summary>
    public int Line { get; set; } = 1;

    /// <summary>Whether it controls the window's macro rather than running a setup of its own.</summary>
    [JsonIgnore] public bool IsControl => Action != BindingAction.StartStop;

    /// <summary>What the key does, for the list: "Pause / resume the window's macro" and so on. Start/stop bindings describe their setup instead.</summary>
    [JsonIgnore] public string ControlText => Action switch
    {
        BindingAction.Pause => "Pause / resume the window's run",
        BindingAction.Step => "Step one line of the paused macro",
        BindingAction.GoToLine => "Go to line " + Line + " of the window's macro",
        _ => ""
    };

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
    // Version 2 (3.1) added bindings that pause, step or move the window's macro.
    const int CurrentVersion = 2;

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
            // Bindings saved before 3.0 may hold old check steps.
            foreach (var binding in file.Bindings) MacroUpgrade.Upgrade(binding.Setup.Steps);
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
