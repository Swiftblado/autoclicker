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
}
