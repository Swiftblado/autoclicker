using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AutoClicker.Core;

/// <summary>
/// The saved macros listed in the Macro tab: one JSON file each, named after the macro, in the
/// app's Macros folder. Macros saved there with Save... in earlier versions show up too.
/// </summary>
public static class MacroLibrary
{
    public const int MaxNameLength = 60;

    static string Folder => Macro.DefaultFolder;

    /// <summary>Before 3.0 the macro being edited was kept in the library folder under this name.</summary>
    public const string OldWorkingCopyName = "current";

    /// <summary>Every saved macro's name, A to Z.</summary>
    public static List<string> Names()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];
            return Directory.EnumerateFiles(Folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Where(name => !name.Equals(OldWorkingCopyName, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The name as it will be saved, or null with <paramref name="problem"/> set when it can't be used.</summary>
    public static string? CleanName(string? name, out string? problem)
    {
        problem = null;
        string clean = (name ?? "").Trim();
        if (clean.Length == 0) { problem = "Give the macro a name."; return null; }
        if (clean.Length > MaxNameLength) { problem = "Keep the name under " + MaxNameLength + " characters."; return null; }
        char[] bad = [.. Path.GetInvalidFileNameChars(), '/', '\\', ':', '*', '?', '"', '<', '>', '|'];
        if (clean.IndexOfAny(bad) >= 0) { problem = "A name can't contain / \\ : * ? \" < > or |."; return null; }
        if (clean.Equals(OldWorkingCopyName, StringComparison.OrdinalIgnoreCase) || clean.Trim('.').Length == 0)
        {
            problem = "That name is reserved; pick another.";
            return null;
        }
        return clean;
    }

    public static string PathOf(string name) => Path.Combine(Folder, name + ".json");

    public static bool Exists(string name) => File.Exists(PathOf(name));

    public static Macro? Load(string name) => Macro.Load(PathOf(name));

    public static void Save(string name, Macro macro)
    {
        var copy = macro.Clone();
        copy.Name = name;
        copy.Save(PathOf(name));
    }

    public static void Rename(string from, string to)
    {
        var macro = Load(from) ?? throw new IOException("Couldn't read \"" + from + "\".");
        Save(to, macro);
        if (!from.Equals(to, StringComparison.OrdinalIgnoreCase)) File.Delete(PathOf(from));
    }

    public static void Delete(string name) => File.Delete(PathOf(name));

    /// <summary><paramref name="name"/>, or "name (2)", "name (3)"… if that's taken.</summary>
    public static string FreeName(string name)
    {
        if (!Exists(name)) return name;
        for (int n = 2; ; n++)
        {
            string candidate = name + " (" + n + ")";
            if (!Exists(candidate)) return candidate;
        }
    }
}
