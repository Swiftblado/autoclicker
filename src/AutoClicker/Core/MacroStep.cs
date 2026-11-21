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
    /// <summary>
    /// Old macros only (before 3.0): look at the screen and act on it. Now a check lives inside a
    /// <see cref="Condition"/>; <see cref="MacroUpgrade"/> turns these into If/While blocks on load.
    /// </summary>
    Check,
    /// <summary>Runs the lines below (up to the matching Else if, Else or End if) when its condition is true.</summary>
    If,
    /// <summary>Inside an If: tried when everything above it in the same If was false.</summary>
    ElseIf,
    /// <summary>Inside an If: runs when nothing above it in the same If was true.</summary>
    Else,
    EndIf,
    /// <summary>Repeats the lines up to End while for as long as its condition stays true.</summary>
    While,
    EndWhile,
    /// <summary>Repeats the lines up to End for a number of times, or for a number of seconds.</summary>
    For,
    EndFor,
    /// <summary>Carries on from another line.</summary>
    GoTo,
    /// <summary>Ends the whole run, like pressing Stop.</summary>
    Stop
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

/// <summary>Which spot of what a check found a click or drag goes to.</summary>
public enum CheckSpot
{
    /// <summary>The middle of what was found.</summary>
    Center,
    /// <summary>The middle, moved by <see cref="MacroStep.OffsetX"/>, <see cref="MacroStep.OffsetY"/> pixels (+ is right and up).</summary>
    OffsetPixels,
    /// <summary>
    /// The middle, moved by a share of the found thing's size: 100 is its right (X) or top (Y)
    /// edge, -100 its left or bottom edge.
    /// </summary>
    OffsetPercent
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

    /// <summary>A check that aims a click or drag: which spot of what it found to use.</summary>
    public CheckSpot Spot { get; set; } = CheckSpot.Center;
    /// <summary>With <see cref="CheckSpot.OffsetPixels"/>: pixels, + is right. With <see cref="CheckSpot.OffsetPercent"/>: percent, 100 = right edge.</summary>
    public int OffsetX { get; set; }
    /// <summary>With <see cref="CheckSpot.OffsetPixels"/>: pixels, + is up. With <see cref="CheckSpot.OffsetPercent"/>: percent, 100 = top edge.</summary>
    public int OffsetY { get; set; }

    // Click / Drag aimed by a screen check
    /// <summary>Click: clicks where this check finds its target. Drag: starts there. Null uses <see cref="From"/>.</summary>
    public MacroStep? FromCheck { get; set; }
    /// <summary>Drag: ends where this check finds its target. Null uses <see cref="To"/>.</summary>
    public MacroStep? ToCheck { get; set; }

    // If / Else if / While
    /// <summary>What an If, Else if or While tests: screen checks joined with and / or / not.</summary>
    public Condition? Condition { get; set; }

    // While
    /// <summary>Least time between two checks of a While's condition, in ms. 0 checks nonstop.</summary>
    public int IntervalMs { get; set; } = DefaultIntervalMs;
    /// <summary>How long a While may keep looping, in ms; then the macro carries on after it. 0 = no limit.</summary>
    public int LimitMs { get; set; } = DefaultLimitMs;

    public const int DefaultIntervalMs = 100, DefaultLimitMs = 60_000;
    /// <summary>Below this, checking a While's condition starts to cost real CPU time.</summary>
    public const int BusyIntervalMs = 10;

    // For
    /// <summary>For: count seconds (<see cref="DurationMs"/>) rather than rounds (<see cref="Count"/>).</summary>
    public bool ForSeconds { get; set; }
    /// <summary>For: how many rounds.</summary>
    public int Count { get; set; } = 10;

    // Go to
    /// <summary>Go to: the line to carry on from, counted from 1.</summary>
    public int Line { get; set; } = 1;

    [JsonIgnore] public PixelRect Area => new(AreaX, AreaY, AreaWidth, AreaHeight);

    /// <summary>If, Else if, Else, End if, While, End while, For, End for, Go to and Stop.</summary>
    [JsonIgnore] public bool IsControl => Kind >= StepKind.If;

    /// <summary>The first line of a block: If, While or For.</summary>
    [JsonIgnore] public bool OpensBlock => Kind is StepKind.If or StepKind.While or StepKind.For;

    /// <summary>The line that closes a block of this kind, or null if it isn't a block's first line.</summary>
    [JsonIgnore] public StepKind? Closer => Kind switch
    {
        StepKind.If => StepKind.EndIf,
        StepKind.While => StepKind.EndWhile,
        StepKind.For => StepKind.EndFor,
        _ => null
    };

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

    public static MacroStep MakeIf(Condition condition, bool elseIf = false) =>
        new() { Kind = elseIf ? StepKind.ElseIf : StepKind.If, Condition = condition };

    public static MacroStep MakeWhile(Condition condition, int intervalMs, int limitMs) =>
        new() { Kind = StepKind.While, Condition = condition, IntervalMs = intervalMs, LimitMs = limitMs };

    public static MacroStep MakeForTimes(int count) =>
        new() { Kind = StepKind.For, Count = count };

    public static MacroStep MakeForSeconds(int ms) =>
        new() { Kind = StepKind.For, ForSeconds = true, DurationMs = ms };

    public static MacroStep MakeGoTo(int line) => new() { Kind = StepKind.GoTo, Line = line };

    public static MacroStep Make(StepKind kind) => new() { Kind = kind };

    /// <summary>A fresh screen check, as kept inside a <see cref="Condition"/>.</summary>
    public static MacroStep NewCheck() => new() { Kind = StepKind.Check, Where = CheckArea.Point };

    /// <summary>The one-line form shown in the step list.</summary>
    public string Describe()
    {
        switch (Kind)
        {
            case StepKind.Click:
                string what = ClickCount > 1 ? "Double-click" : "Click";
                if (ClickHold.IsHalf(HoldMs)) what = "Half " + what.ToLowerInvariant();
                string where = FromCheck != null ? "on " + FromCheck.DescribeAim()
                    : AtCursor ? "at the cursor" : Point(From);
                string held = HoldMs > 0 ? ", held " + Time(HoldMs) : "";
                return what + " " + Button.ToString().ToLowerInvariant() + " " + where + held;

            case StepKind.Drag:
                return "Drag " + Button.ToString().ToLowerInvariant() + " " +
                       (FromCheck != null ? "from " + FromCheck.DescribeAim() : Point(From)) +
                       " to " + (ToCheck != null ? ToCheck.DescribeAim() : Point(To)) + " over " + Time(DurationMs);

            case StepKind.Move:
                return "Move cursor to " + Point(To);

            case StepKind.Key:
                string keys = Modifiers == Modifiers.None ? "" : DescribeModifiers() + " + ";
                string hold = DurationMs > 0 ? " (hold " + Time(DurationMs) + ")" : "";
                return "Press " + keys + KeyMap.Name(Key) + hold;

            case StepKind.Wait:
                return "Wait " + Time(DurationMs);

            case StepKind.Check:
                return "Look for " + DescribeLook();

            case StepKind.If:
                return "If " + DescribeCondition();
            case StepKind.ElseIf:
                return "Else if " + DescribeCondition();
            case StepKind.Else:
                return "Else";
            case StepKind.EndIf:
                return "End if";

            case StepKind.While:
                string every = IntervalMs <= 0 ? "checked nonstop" : "checked every " + Time(IntervalMs);
                string limit = LimitMs > 0 ? ", up to " + Time(LimitMs) : "";
                return "While " + DescribeCondition() + "   (" + every + limit + ")";
            case StepKind.EndWhile:
                return "End while";

            case StepKind.For:
                return ForSeconds
                    ? "For " + Time(DurationMs)
                    : "For " + Count.ToString(CultureInfo.CurrentCulture) + (Count == 1 ? " time" : " times");
            case StepKind.EndFor:
                return "End for";

            case StepKind.GoTo:
                return "Go to line " + Line.ToString(CultureInfo.CurrentCulture);
            case StepKind.Stop:
                return "Stop the macro";

            default:
                return Kind.ToString();
        }
    }

    string DescribeCondition() => Condition?.Describe() ?? "(no condition)";

    /// <summary>What a check looks for and where, e.g. "#FF0000 at (10, 20)" or "image anywhere on screen".</summary>
    public string DescribeLook()
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
        return what + where;
    }

    /// <summary>For a check that aims a click: what it finds and which spot, e.g. "#FF0000 in (0, 0) 200×200 (center +10, -5 px)".</summary>
    public string DescribeAim() => DescribeLook() + " (" + DescribeSpot() + ")";

    /// <summary>"center", "center +10, -5 px" or "center +50% X, -20% Y".</summary>
    public string DescribeSpot() => Spot switch
    {
        CheckSpot.OffsetPixels => "center " + Signed(OffsetX) + ", " + Signed(OffsetY) + " px",
        CheckSpot.OffsetPercent => "center " + Signed(OffsetX) + "% X, " + Signed(OffsetY) + "% Y",
        _ => "center"
    };

    static string Signed(int n) => (n > 0 ? "+" : "") + n.ToString(CultureInfo.CurrentCulture);

    /// <summary>The screen checks that aim this click or drag, if any.</summary>
    [JsonIgnore] public IEnumerable<MacroStep> AimChecks =>
        new[] { FromCheck, Kind == StepKind.Drag ? ToCheck : null }.OfType<MacroStep>();

    /// <summary>What's missing from a screen check, in words for the user; null when it's complete.</summary>
    public string? CheckProblem() => Target switch
    {
        CheckTarget.Color when ColorMatch.Normalize(Color) == null => "has no valid color. Use a hex code like #FF8800.",
        CheckTarget.Colors when Points.Count == 0 => "has no colors to look for. Add at least one point.",
        CheckTarget.Image when string.IsNullOrEmpty(ImagePng) => "has no image. Capture one or load a PNG.",
        _ => null
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
        clone.Condition = Condition?.Clone();
        clone.FromCheck = FromCheck?.Clone();
        clone.ToCheck = ToCheck?.Clone();
        return clone;
    }
}
