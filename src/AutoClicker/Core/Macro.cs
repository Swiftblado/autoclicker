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

    /// <summary>
    /// After steps move or go, keeps every check's "go to step" pointing at the same step.
    /// <paramref name="map"/> takes an old step number (from 1) to its new one.
    /// </summary>
    public static void Renumber(List<MacroStep> steps, Func<int, int> map)
    {
        foreach (var step in steps)
        {
            if (step.Kind != StepKind.Check) continue;
            step.FoundStep = Math.Clamp(map(step.FoundStep), 1, Math.Max(1, steps.Count));
            step.MissingStep = Math.Clamp(map(step.MissingStep), 1, Math.Max(1, steps.Count));
        }
    }

    /// <summary>What's wrong with a check step, in words for the user; null when it can run.</summary>
    public static string? CheckProblem(List<MacroStep> steps)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.Kind != StepKind.Check) continue;
            string which = "Step " + (i + 1) + " ";
            switch (step.Target)
            {
                case CheckTarget.Color when ColorMatch.Normalize(step.Color) == null:
                    return which + "has no valid color. Use a hex code like #FF8800.";
                case CheckTarget.Colors when step.Points.Count == 0:
                    return which + "has no colors to look for. Add at least one point.";
                case CheckTarget.Image when string.IsNullOrEmpty(step.ImagePng):
                    return which + "has no image. Capture one or load a PNG.";
            }
            if (step.OnFound == CheckBranch.GoTo && (step.FoundStep < 1 || step.FoundStep > steps.Count) ||
                step.OnMissing == CheckBranch.GoTo && (step.MissingStep < 1 || step.MissingStep > steps.Count))
                return which + "goes to a step that doesn't exist.";
        }
        return null;
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
                else if (step.Kind == StepKind.Click)
                    total += Math.Max(0, step.HoldMs) * Math.Max(1, step.ClickCount);
            }
            return total;
        }
    }
}
