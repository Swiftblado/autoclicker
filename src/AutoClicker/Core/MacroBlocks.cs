using System;
using System.Collections.Generic;

namespace AutoClicker.Core;

/// <summary>
/// How a macro's lines nest: which If, While and For each line sits inside, and where each
/// block's other parts are. Worked out from the flat list of lines, which is how macros are
/// stored, shown (indented) and run.
/// </summary>
public sealed class MacroBlocks
{
    /// <summary>How deep each line is indented: 0 at the top level, 1 inside one block, and so on.</summary>
    public int[] Depth { get; }

    /// <summary>
    /// For If and Else if: the next part of the same If (Else if, Else or End if).
    /// For While and For: their End. -1 elsewhere.
    /// </summary>
    public int[] Next { get; }

    /// <summary>For every part of an If (If, Else if, Else): its End if. For While and For: their End. -1 elsewhere.</summary>
    public int[] End { get; }

    /// <summary>For End if, End while and End for, and for Else if and Else: the If, While or For they belong to. -1 elsewhere.</summary>
    public int[] Open { get; }

    /// <summary>What's wrong with the nesting, in words for the user; null when every block is closed properly.</summary>
    public string? Problem { get; }

    MacroBlocks(string? problem, int[] depth, int[] next, int[] end, int[] open)
    {
        Depth = depth;
        Next = next;
        End = end;
        Open = open;
        Problem = problem;
    }

    sealed class Frame(int index, StepKind kind)
    {
        public int Index { get; } = index;
        public StepKind Kind { get; } = kind;
        public int LastPart { get; set; } = index;
        public bool SawElse { get; set; }
        public List<int> Parts { get; } = [index];
    }

    public static MacroBlocks Analyze(IReadOnlyList<MacroStep> steps)
    {
        int n = steps.Count;
        var depth = new int[n];
        var next = Filled(n);
        var end = Filled(n);
        var open = Filled(n);
        string? problem = null;
        var stack = new List<Frame>();

        void Fail(string text) => problem ??= text;
        static string LineName(int i) => "line " + (i + 1);

        for (int i = 0; i < n; i++)
        {
            var kind = steps[i].Kind;
            var top = stack.Count > 0 ? stack[^1] : null;
            switch (kind)
            {
                case StepKind.If or StepKind.While or StepKind.For:
                    depth[i] = stack.Count;
                    stack.Add(new Frame(i, kind));
                    break;

                case StepKind.ElseIf or StepKind.Else:
                    string name = kind == StepKind.Else ? "Else" : "Else if";
                    if (top?.Kind != StepKind.If)
                    {
                        depth[i] = stack.Count;
                        Fail("The " + name + " on " + LineName(i) + " isn't inside an If.");
                        break;
                    }
                    depth[i] = stack.Count - 1;
                    if (top.SawElse) Fail("The " + name + " on " + LineName(i) + " comes after the Else of its If; it has to come before it.");
                    next[top.LastPart] = i;
                    top.LastPart = i;
                    top.Parts.Add(i);
                    open[i] = top.Index;
                    if (kind == StepKind.Else) top.SawElse = true;
                    break;

                case StepKind.EndIf or StepKind.EndWhile or StepKind.EndFor:
                    var opener = kind switch
                    {
                        StepKind.EndIf => StepKind.If,
                        StepKind.EndWhile => StepKind.While,
                        _ => StepKind.For
                    };
                    if (top?.Kind != opener)
                    {
                        depth[i] = Math.Max(0, stack.Count - 1);
                        Fail(top == null
                            ? "The " + Name(kind) + " on " + LineName(i) + " has no " + Name(opener) + " above it."
                            : "The " + Name(kind) + " on " + LineName(i) + " closes the wrong block: the " +
                              Name(top.Kind) + " on " + LineName(top.Index) + " needs its " + Name(CloserOf(top.Kind)) + " first.");
                        break;
                    }
                    stack.RemoveAt(stack.Count - 1);
                    depth[i] = stack.Count;
                    next[top.LastPart] = i;
                    foreach (int part in top.Parts) end[part] = i;
                    open[i] = top.Index;
                    break;

                default:
                    depth[i] = stack.Count;
                    break;
            }
        }

        if (stack.Count > 0)
        {
            var unclosed = stack[0];
            Fail("The " + Name(unclosed.Kind) + " on " + LineName(unclosed.Index) + " has no " + Name(CloserOf(unclosed.Kind)) + ".");
        }
        return new MacroBlocks(problem, depth, next, end, open);
    }

    static StepKind CloserOf(StepKind kind) => kind switch
    {
        StepKind.If => StepKind.EndIf,
        StepKind.While => StepKind.EndWhile,
        _ => StepKind.EndFor
    };

    public static string Name(StepKind kind) => kind switch
    {
        StepKind.If => "If",
        StepKind.ElseIf => "Else if",
        StepKind.Else => "Else",
        StepKind.EndIf => "End if",
        StepKind.While => "While",
        StepKind.EndWhile => "End while",
        StepKind.For => "For",
        StepKind.EndFor => "End for",
        StepKind.GoTo => "Go to",
        _ => kind.ToString()
    };

    static int[] Filled(int n)
    {
        var array = new int[n];
        Array.Fill(array, -1);
        return array;
    }
}
