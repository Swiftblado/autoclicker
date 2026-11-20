using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoClicker.Views;

/// <summary>One line of the macro as shown in the step list: its number, indented to its block's depth.</summary>
sealed record StepRow(int Line, int Depth, string Text, bool IsControl)
{
    const double IndentWidth = 22;

    static readonly IBrush LineBrush = new SolidColorBrush(Color.Parse("#5E6676"));
    static readonly IBrush ControlBrush = new SolidColorBrush(Color.Parse("#60A5FA"));
    static readonly IBrush GuideBrush = new SolidColorBrush(Color.Parse("#2A303C"));

    public static readonly FuncDataTemplate<StepRow> Template = new((row, _) =>
    {
        if (row == null) return new TextBlock();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("34,*") };
        grid.Children.Add(new TextBlock
        {
            Text = row.Line.ToString(),
            Foreground = LineBrush,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        // A thin guide per level, so it's easy to see which block a line belongs to.
        var body = new StackPanel { Orientation = Orientation.Horizontal, [Grid.ColumnProperty] = 1 };
        for (int level = 0; level < row.Depth; level++)
            body.Children.Add(new Border
            {
                Width = IndentWidth,
                BorderBrush = GuideBrush,
                BorderThickness = new Thickness(1, 0, 0, 0),
                Margin = new Thickness(4, -4, 0, -4)
            });
        var text = new TextBlock
        {
            Text = row.Text,
            FontWeight = row.IsControl ? FontWeight.SemiBold : FontWeight.Normal,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        // Only control lines get a color; the rest keep the theme's text color.
        if (row.IsControl) text.Foreground = ControlBrush;
        body.Children.Add(text);
        ToolTip.SetTip(body, row.Text);
        grid.Children.Add(body);
        return grid;
    });
}
