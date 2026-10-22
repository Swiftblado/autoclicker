using System;
using System.Reflection;

namespace AutoClicker.Updates;

/// <summary>
/// Which version this is, and whether it may update itself. Release builds are stamped by CI
/// (<c>-p:Version=… -p:UpdatableBuild=true</c>); anything built locally is a development build,
/// which never replaces itself with a download.
/// </summary>
public static class AppVersion
{
    static readonly Assembly Assembly = typeof(AppVersion).Assembly;

    public static Version Current { get; } = Trim(Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static bool IsUpdatable { get; } = HasMetadata("UpdatableBuild", "true");

    /// <summary>"v2.2.0", or "development build".</summary>
    public static string Display => IsUpdatable ? "v" + Format(Current) : "development build";

    public static string Format(Version v) => v.Build >= 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";

    /// <summary>Drops the unused fourth part, so 2.2.0.0 compares equal to a tag's 2.2.0.</summary>
    static Version Trim(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    static bool HasMetadata(string key, string value)
    {
        foreach (var attribute in Assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == key)
                return string.Equals(attribute.Value, value, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }
}
