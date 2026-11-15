using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    Wait,
    /// <summary>Look at the screen for a color, a pattern of colors, or an image, and act on what's there.</summary>
    Check
}

/// <summary>What a <see cref="StepKind.Check"/> looks for.</summary>
public enum CheckTarget
{
    /// <summary>One color, given as a hex code.</summary>
    Color,
    /// <summary>Several colors at set points. All of them must match.</summary>
    Colors,
    /// <summary>A picture: captured off the screen or loaded from a file.</summary>
    Image
}

/// <summary>Where a check looks.</summary>
public enum CheckArea
{
    /// <summary>
    /// Color: the one pixel at From. Colors: exactly at each point's own coordinates.
    /// Not used for images.
    /// </summary>
    Point,
    /// <summary>Anywhere inside the rectangle AreaX, AreaY, AreaWidth, AreaHeight.</summary>
    Area,
    /// <summary>Anywhere on screen.</summary>
    Screen
}

/// <summary>Where a macro goes after a check.</summary>
public enum CheckBranch
{
    /// <summary>On to the following step.</summary>
    Next,
    /// <summary>To a chosen step, earlier or later.</summary>
    GoTo,
    /// <summary>End the run, like pressing Stop.</summary>
    Stop
}

/// <summary>One point of a multi-color check: a screen position and the color expected there.</summary>
public sealed class ColorPoint
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Color { get; set; } = "#000000";

    public ColorPoint Clone() => (ColorPoint)MemberwiseClone();
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

    /// <summary>
    /// Click only: how long each press is held, in ms. 0 is a half click. Macros saved before
    /// this existed have no value, so they get the default full click.
    /// </summary>
    public int HoldMs { get; set; } = ClickHold.DefaultMs;

    // Drag / Move destination
    public int ToX { get; set; }
    public int ToY { get; set; }

    // Key
    public Key Key { get; set; } = Key.None;
    public Modifiers Modifiers { get; set; } = Modifiers.None;

    /// <summary>Drag travel time, key hold time, or wait length — depending on <see cref="Kind"/>.</summary>
    public int DurationMs { get; set; }

    // Check
    public CheckTarget Target { get; set; }
    public CheckArea Where { get; set; }
    /// <summary>Color checks: "#RRGGBB".</summary>
    public string Color { get; set; } = "#FF0000";
    /// <summary>
    /// Multi-color checks. Searched for in an area they keep their spacing, and the first point
    /// is where the pattern counts as found.
    /// </summary>
    public List<ColorPoint> Points { get; set; } = new();
    /// <summary>Image checks: the picture, as a base64 PNG.</summary>
    public string? ImagePng { get; set; }
    public int AreaX { get; set; }
    public int AreaY { get; set; }
    public int AreaWidth { get; set; }
    public int AreaHeight { get; set; }
    /// <summary>How far each of red, green and blue may be off and still match, 0–255.</summary>
    public int Tolerance { get; set; } = ColorMatch.DefaultTolerance;
    /// <summary>Keep looking until it shows up, rather than looking once.</summary>
    public bool WaitUntilFound { get; set; }
    /// <summary>With <see cref="WaitUntilFound"/>: give up after this long. 0 waits for as long as it takes.</summary>
    public int TimeoutMs { get; set; } = 10000;
    /// <summary>Click <see cref="Button"/> on whatever was found.</summary>
    public bool ClickFound { get; set; }
    public CheckBranch OnFound { get; set; } = CheckBranch.Next;
    /// <summary>Step number (counted from 1) to go to when found, for <see cref="CheckBranch.GoTo"/>.</summary>
    public int FoundStep { get; set; } = 1;
    public CheckBranch OnMissing { get; set; } = CheckBranch.Next;
    /// <summary>Step number (counted from 1) to go to when not found.</summary>
    public int MissingStep { get; set; } = 1;

    [JsonIgnore] public PixelRect Area => new(AreaX, AreaY, AreaWidth, AreaHeight);

    [JsonIgnore] public PixelPoint From => new(FromX, FromY);
    [JsonIgnore] public PixelPoint To => new(ToX, ToY);

    public static MacroStep MakeClick(ClickButton button, int clickCount, PixelPoint at, bool atCursor = false,
        int holdMs = ClickHold.DefaultMs) =>
        new()
        {
            Kind = StepKind.Click, Button = button, ClickCount = clickCount,
            AtCursor = atCursor, FromX = at.X, FromY = at.Y, HoldMs = holdMs
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
                if (ClickHold.IsHalf(HoldMs)) what = "Half " + what.ToLowerInvariant();
                string where = AtCursor ? "at the cursor" : Point(From);
                string held = HoldMs > 0 ? ", held " + Time(HoldMs) : "";
                return what + " " + Button.ToString().ToLowerInvariant() + " " + where + held;

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

            case StepKind.Check:
                return DescribeCheck();

            default:
                return Kind.ToString();
        }
    }

    string DescribeCheck()
    {
        string what = Target switch
        {
            CheckTarget.Color => Color.ToUpperInvariant(),
            CheckTarget.Colors => Points.Count + (Points.Count == 1 ? " color" : " colors"),
            _ => "image"
        };
        string where = (Target, Where) switch
        {
            (CheckTarget.Color, CheckArea.Point) => " at " + Point(From),
            (CheckTarget.Colors, CheckArea.Point) => " at their points",
            (_, CheckArea.Screen) => " anywhere on screen",
            _ => " in " + Point(new PixelPoint(AreaX, AreaY)) + " " + AreaWidth + "×" + AreaHeight
        };

        string found = Branch(OnFound, FoundStep);
        if (ClickFound) found = Button.ToString().ToLowerInvariant() + " click it" + (found == "" ? "" : ", " + found);
        if (found == "") found = "continue";
        string missing = Branch(OnMissing, MissingStep);
        if (missing == "") missing = "continue";

        if (WaitUntilFound)
        {
            string limit = TimeoutMs > 0 ? " (up to " + Time(TimeoutMs) + ")" : "";
            return "Wait for " + what + where + limit + " → " + found +
                   (TimeoutMs > 0 ? "; not seen → " + missing : "");
        }
        return "If " + what + where + " → " + found + "; else → " + missing;
    }

    static string Branch(CheckBranch branch, int step) => branch switch
    {
        CheckBranch.GoTo => "go to step " + step,
        CheckBranch.Stop => "stop",
        _ => ""
    };

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

    public MacroStep Clone()
    {
        var clone = (MacroStep)MemberwiseClone();
        clone.Points = Points.Select(point => point.Clone()).ToList();
        return clone;
    }
}
