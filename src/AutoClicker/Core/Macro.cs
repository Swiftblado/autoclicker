using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutoClicker.Core;

/// <summary>A saved sequence of <see cref="MacroStep"/>s, persisted as JSON.</summary>
public sealed class Macro
{
    /// <summary>Bumped when the on-disk shape changes; <see cref="Load"/> rejects a file newer than this.</summary>
    const int CurrentVersion = 1;

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public int Version { get; set; } = CurrentVersion;
    public string? Name { get; set; }
    public List<MacroStep> Steps { get; set; } = new();

    /// <summary>"Macros" folder next to the settings file; used as the file dialog's starting folder.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AutoClicker", "Macros");

    public static Macro? Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var macro = JsonSerializer.Deserialize<Macro>(json, JsonOptions);
            if (macro is null || macro.Version > CurrentVersion) return null;
            return macro;
        }
        catch (Exception)
        {
            // Bad JSON, missing file, wrong shape, unreadable — caller just sees "no macro".
            return null;
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        Version = CurrentVersion;
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Macro Clone()
    {
        var clone = new Macro { Version = Version, Name = Name };
        foreach (var step in Steps) clone.Steps.Add(step.Clone());
        return clone;
    }

    /// <summary>How long one pass through the macro takes: every step's own time cost, summed.</summary>
    public int TotalDurationMs
    {
        get
        {
            int total = 0;
            foreach (var step in Steps)
            {
                if (step.Kind is StepKind.Wait or StepKind.Drag or StepKind.Key)
                    total += step.DurationMs;
            }
            return total;
        }
    }
}
