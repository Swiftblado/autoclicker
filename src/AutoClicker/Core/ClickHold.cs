namespace AutoClicker.Core;

/// <summary>
/// How long a "full" click holds the button down. A half click (0 ms) presses and releases in
/// the same instant, which some apps and games read as a light tap or miss entirely; holding
/// for at least a frame makes it register like a real press.
/// </summary>
public static class ClickHold
{
    /// <summary>The choices offered in the UI. 20 ms is just over a frame at 60 fps.</summary>
    public static readonly int[] ChoicesMs = [20, 35, 50];

    public const int DefaultMs = 20;

    public const int HalfClickMs = 0;

    /// <summary>
    /// A hold shorter than one frame at 60 fps can fall between a game's looks at the mouse, so a
    /// custom hold under this behaves like a half click and is labelled as one.
    /// </summary>
    public const int HalfBelowMs = 17;

    public const int MaxCustomMs = 1000;

    public static bool IsHalf(int holdMs) => holdMs < HalfBelowMs;

    /// <summary>The "?" explanation next to the hold time.</summary>
    public const string HelpText =
        "Most apps and websites count any click, even a half click. Games usually check the mouse once " +
        "per frame, so a press shorter than one frame (about 17 ms at 60 fps, 33 ms at 30 fps) can be " +
        "missed. If a game misses clicks, try 35 or 50 ms.";
}
