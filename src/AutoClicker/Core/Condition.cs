using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AutoClicker.Core;

/// <summary>How the items of a condition group combine.</summary>
public enum LogicJoin
{
    /// <summary>All of them must be true.</summary>
    And,
    /// <summary>At least one must be true.</summary>
    Or
}

/// <summary>
/// What an If, Else if or While tests. Either one screen check (<see cref="Check"/> set) or a group
/// of conditions joined by "and" or "or" (<see cref="Items"/>). Groups nest, so any mix like
/// (A and not B) or C can be built. Each one can be turned around with <see cref="Not"/>.
/// </summary>
public sealed class Condition
{
    /// <summary>True when the check or group is false, and the other way round.</summary>
    public bool Not { get; set; }

    /// <summary>A single screen check (a <see cref="StepKind.Check"/> step's look-for settings). Null for a group.</summary>
    public MacroStep? Check { get; set; }

    /// <summary>For a group: how its items combine.</summary>
    public LogicJoin Join { get; set; } = LogicJoin.And;

    /// <summary>For a group: its checks and nested groups.</summary>
    public List<Condition> Items { get; set; } = new();

    [JsonIgnore] public bool IsGroup => Check == null;

    public static Condition Group(LogicJoin join = LogicJoin.And, params Condition[] items) =>
        new() { Join = join, Items = items.ToList() };

    public static Condition Of(MacroStep check, bool not = false) => new() { Check = check, Not = not };

    public Condition Clone() => new()
    {
        Not = Not,
        Check = Check?.Clone(),
        Join = Join,
        Items = Items.Select(item => item.Clone()).ToList()
    };

    /// <summary>Every screen check in here, nested groups included.</summary>
    public IEnumerable<MacroStep> Checks() =>
        Check != null ? [Check] : Items.SelectMany(item => item.Checks());

    /// <summary>
    /// Works the condition out, asking <paramref name="found"/> about each check it needs. Stops
    /// early once the answer is settled, so later (possibly slow) checks are skipped.
    /// </summary>
    public bool Evaluate(Func<MacroStep, bool> found)
    {
        bool value = Check != null
            ? found(Check)
            : Join == LogicJoin.And ? Items.All(item => item.Evaluate(found)) : Items.Any(item => item.Evaluate(found));
        return value != Not;
    }

    /// <summary>
    /// In words, with brackets around nested groups: "(#FF0000 at (10, 20) and not image anywhere on screen) or …".
    /// </summary>
    public string Describe() => Describe(top: true);

    string Describe(bool top)
    {
        string text;
        if (Check != null) text = Check.DescribeLook();
        else if (Items.Count == 0) text = "(empty)";
        else
        {
            string join = Join == LogicJoin.And ? " and " : " or ";
            text = string.Join(join, Items.Select(item => item.Describe(top: false)));
            // A lone item needs no brackets; neither does the top level, unless "not" applies to all of it.
            if (Items.Count > 1 && (!top || Not)) text = "(" + text + ")";
        }
        return Not ? "not " + text : text;
    }

    /// <summary>What's wrong, in words for the user; null when it can run.</summary>
    public string? Problem()
    {
        if (Check != null) return Check.CheckProblem() is { } p ? "A check (" + Check.DescribeLook() + ") " + p : null;
        if (Items.Count == 0) return "A condition (or a group in it) has no checks yet.";
        foreach (var item in Items)
            if (item.Problem() is { } p) return p;
        return null;
    }
}
