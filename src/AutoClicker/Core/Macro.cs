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
    // Version 3 (3.1) added checks that aim clicks, and a separate "and"/"or" for each condition item.
    const int CurrentVersion = 3;

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
            macro.Upgrade();
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

    /// <summary>
    /// Set when loading turned old (pre-3.0) check steps into If/While blocks: what the user should
    /// know about it. Not saved.
    /// </summary>
    [JsonIgnore] public string? UpgradeNote { get; private set; }

    /// <summary>Brings steps from an older version up to date; see <see cref="MacroUpgrade"/>.</summary>
    public void Upgrade()
    {
        int dropped = MacroUpgrade.Upgrade(Steps);
        if (dropped < 0) return;
        UpgradeNote = "Its screen checks were turned into If and While blocks." + (dropped > 0
            ? " " + dropped + (dropped == 1 ? " check" : " checks") + " searching an area clicked where they found something; checks can't do that anymore, so " + (dropped == 1 ? "that click was" : "those clicks were") + " left out."
            : "");
    }

    /// <summary>The macro as it would be written to a file.</summary>
    public string ToJson()
    {
        Version = CurrentVersion;
        return JsonSerializer.Serialize(this, JsonOptions);
    }

    public Macro Clone()
    {
        var clone = new Macro { Version = Version, Name = Name };
        foreach (var step in Steps) clone.Steps.Add(step.Clone());
        return clone;
    }

    /// <summary>
    /// After lines move, appear or go, keeps every "Go to line" pointing at the same line.
    /// <paramref name="map"/> takes an old line number (from 1) to its new one.
    /// </summary>
    public static void Renumber(List<MacroStep> steps, Func<int, int> map)
    {
        foreach (var step in steps)
            if (step.Kind == StepKind.GoTo)
                step.Line = Math.Clamp(map(step.Line), 1, Math.Max(1, steps.Count));
    }

    /// <summary>What stops the macro from running, in words for the user; null when it can run.</summary>
    public static string? Problem(List<MacroStep> steps)
    {
        var blocks = MacroBlocks.Analyze(steps);
        if (blocks.Problem != null) return blocks.Problem;
        for (int i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            string which = "Line " + (i + 1) + " ";
            switch (step.Kind)
            {
                case StepKind.If or StepKind.ElseIf or StepKind.While:
                    if (step.Condition == null) return which + "has no condition. Double-click it to set one.";
                    if (step.Condition.Problem() is { } trouble) return "Line " + (i + 1) + ": " + trouble;
                    break;
                case StepKind.Click or StepKind.Drag:
                    foreach (var aim in step.AimChecks)
                        if (aim.CheckProblem() is { } missing)
                            return which + "aims at a screen check (" + aim.DescribeLook() + ") that " + missing;
                    break;
                case StepKind.GoTo when step.Line < 1 || step.Line > steps.Count:
                    return which + "goes to a line that doesn't exist.";
                case StepKind.Check:
                    return which + "is a screen check on its own; checks belong in the condition of an If or While.";
            }
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
