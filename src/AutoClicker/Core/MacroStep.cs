using System;
using System.Globalization;
using System.Text.Json.Serialization;
using AutoClicker.Input;
using Avalonia;
using Avalonia.Input;

namespace AutoClicker.Core;

public enum StepKind
{
    /// <summary>Press and release a mouse button, once or twice.</summary>
    Click,
    /// <summary>Hold a mouse button down while the cursor travels from one point to another.</summary>
    Drag,
    /// <summary>Move the cursor without pressing anything.</summary>
    Move,
    /// <summary>Press and release a key, with optional modifiers held around it.</summary>
    Key,
    /// <summary>Do nothing for a while.</summary>
    Wait
}

/// <summary>One action in a macro. Plain data so it round-trips through JSON.</summary>
public sealed class MacroStep
{
    public StepKind Kind { get; set; }

    // Click / Drag
    public ClickButton Button { get; set; } = ClickButton.Left;
    public int ClickCount { get; set; } = 1;
    /// <summary>When true the step uses wherever the cursor happens to be, ignoring <see cref="From"/>.</summary>
    public bool AtCursor { get; set; }
    public int FromX { get; set; }
    public int FromY { get; set; }

    // Drag / Move destination
    public int ToX { get; set; }
    public int ToY { get; set; }

    // Key
    public Key Key { get; set; } = Key.None;
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    /// <summary>Drag travel time, key hold time, or wait length — depending on <see cref="Kind"/>.</summary>
    public int DurationMs { get; set; }

    [JsonIgnore] public PixelPoint From => new(FromX, FromY);
    [JsonIgnore] public PixelPoint To => new(ToX, ToY);

    public static MacroStep MakeClick(ClickButton button, int clickCount, PixelPoint at, bool atCursor = false) =>
        new()
        {
            Kind = StepKind.Click, Button = button, ClickCount = clickCount,
            AtCursor = atCursor, FromX = at.X, FromY = at.Y
        };

    public static MacroStep MakeDrag(ClickButton button, PixelPoint from, PixelPoint to, int durationMs) =>
        new()
        {
            Kind = StepKind.Drag, Button = button,
            FromX = from.X, FromY = from.Y, ToX = to.X, ToY = to.Y,
            DurationMs = durationMs
        };

    public static MacroStep MakeMove(PixelPoint to) =>
        new() { Kind = StepKind.Move, ToX = to.X, ToY = to.Y };

    public static MacroStep MakeKey(Key key, Modifiers modifiers, int holdMs) =>
        new() { Kind = StepKind.Key, Key = key, Modifiers = modifiers, DurationMs = holdMs };

    public static MacroStep MakeWait(int ms) =>
        new() { Kind = StepKind.Wait, DurationMs = ms };

    /// <summary>The one-line form shown in the step list.</summary>
    public string Describe()
    {
        switch (Kind)
        {
            case StepKind.Click:
                string what = ClickCount > 1 ? "Double-click" : "Click";
                string where = AtCursor ? "at the cursor" : Point(From);
                return what + " " + Button.ToString().ToLowerInvariant() + " " + where;

            case StepKind.Drag:
                return "Drag " + Button.ToString().ToLowerInvariant() + " " + Point(From) +
                       " to " + Point(To) + " over " + Time(DurationMs);

            case StepKind.Move:
                return "Move cursor to " + Point(To);

            case StepKind.Key:
                string keys = Modifiers == Modifiers.None ? "" : DescribeModifiers() + " + ";
                string hold = DurationMs > 0 ? " (hold " + Time(DurationMs) + ")" : "";
                return "Press " + keys + KeyMap.Name(Key) + hold;

            case StepKind.Wait:
                return "Wait " + Time(DurationMs);

            default:
                return Kind.ToString();
        }
    }

    string DescribeModifiers()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (Modifiers.HasFlag(Modifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(Modifiers.Alt)) parts.Add(KeyMap.AltName);
        if (Modifiers.HasFlag(Modifiers.Meta)) parts.Add(KeyMap.MetaName);
        return string.Join(" + ", parts);
    }

    static string Point(PixelPoint p) =>
        "(" + p.X.ToString(CultureInfo.InvariantCulture) + ", " + p.Y.ToString(CultureInfo.InvariantCulture) + ")";

    static string Time(int ms) =>
        ms >= 1000
            ? (ms / 1000.0).ToString("0.##", CultureInfo.CurrentCulture) + " s"
            : ms.ToString(CultureInfo.CurrentCulture) + " ms";

    public MacroStep Clone() => (MacroStep)MemberwiseClone();
}
