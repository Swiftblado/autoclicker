using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AutoClicker.Core;

/// <summary>How a condition joins the one before it.</summary>
public enum LogicJoin
{
    /// <summary>Both must be true.</summary>
    And,
    /// <summary>At least one must be true.</summary>
    Or
}

/// <summary>
/// What an If, Else if or While tests. Either one screen check (<see cref="Check"/> set) or a group
/// of conditions (<see cref="Items"/>), each joined to the one before it by "and" or "or"
/// (<see cref="Link"/>). As usual, "and" goes first: A or B and C means A or (B and C). Groups
/// nest, for the brackets that rule can't give. Each one can be turned around with <see cref="Not"/>.
/// </summary>
public sealed class Condition
{
    /// <summary>True when the check or group is false, and the other way round.</summary>
    public bool Not { get; set; }

    /// <summary>A single screen check (a <see cref="StepKind.Check"/> step's look-for settings). Null for a group.</summary>
    public MacroStep? Check { get; set; }

    /// <summary>
    /// For a group: how an item without its own <see cref="Link"/> joins the one before it. In 3.0
    /// this was the only join, shared by all of a group's items.
    /// </summary>
    public LogicJoin Join { get; set; } = LogicJoin.And;

    /// <summary>For an item of a group: "and" or "or" with the item before it. Unused on the first item.</summary>
    public LogicJoin? Link { get; set; }

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
        Link = Link,
        Items = Items.Select(item => item.Clone()).ToList()
    };

    /// <summary>How item <paramref name="index"/> joins the item before it.</summary>
    public LogicJoin LinkOf(int index) => Items[index].Link ?? Join;

    /// <summary>
    /// Writes every item's join into its own <see cref="Link"/>, all the way down, so a group's
    /// <see cref="Join"/> no longer matters. Done when the editor saves.
    /// </summary>
    public void FixLinks()
    {
        for (int k = 0; k < Items.Count; k++)
        {
            Items[k].Link = k == 0 ? null : LinkOf(k);
            Items[k].FixLinks();
        }
    }

    /// <summary>Every screen check in here, nested groups included.</summary>
    public IEnumerable<MacroStep> Checks() =>
        Check != null ? [Check] : Items.SelectMany(item => item.Checks());

    /// <summary>
    /// Works the condition out, asking <paramref name="found"/> about each check it needs. Stops
    /// early once the answer is settled, so later (possibly slow) checks are skipped.
    /// </summary>
    public bool Evaluate(Func<MacroStep, bool> found)
    {
        bool value;
        if (Check != null) value = found(Check);
        else if (Items.Count == 0) value = Join == LogicJoin.And;
        else
        {
            // "and" goes first: the items fall into runs joined by "and", and the group is true
            // when any run is. Once a run has failed, the rest of it isn't looked at.
            value = false;
            bool run = true;
            for (int k = 0; k < Items.Count; k++)
            {
                if (k > 0 && LinkOf(k) == LogicJoin.Or)
                {
                    if (run) { value = true; break; }
                    run = true;
                }
                if (run) run = Items[k].Evaluate(found);
            }
            value |= run;
        }
        return value != Not;
    }

    /// <summary>
    /// In words, with brackets showing what goes together: "#FF0000 at (10, 20) or (not image anywhere on screen and …)".
    /// </summary>
    public string Describe() => Describe(top: true);

    string Describe(bool top)
    {
        string text;
        if (Check != null) text = Check.DescribeLook();
        else if (Items.Count == 0) text = "(empty)";
        else
        {
            // The runs joined by "and"; when "or" comes between runs, brackets show that "and" goes first.
            var runs = new List<List<string>> { new() };
            for (int k = 0; k < Items.Count; k++)
            {
                if (k > 0 && LinkOf(k) == LogicJoin.Or) runs.Add(new());
                runs[^1].Add(Items[k].Describe(top: false));
            }
            text = string.Join(" or ", runs.Select(run =>
                runs.Count > 1 && run.Count > 1 ? "(" + string.Join(" and ", run) + ")" : string.Join(" and ", run)));
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
