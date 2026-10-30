using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AutoClicker.Views;

/// <summary>
/// "What's new": CHANGELOG.md from the repo, built into the app, shown as headings and bullets.
/// Only the shapes the changelog uses are understood: "## " headings and "- " bullets.
/// </summary>
static class ChangelogWindow
{
    public static async Task ShowAsync(Window owner)
    {
        var list = new StackPanel { Spacing = 6, Margin = new Thickness(20, 16, 24, 20) };
        foreach (var raw in ReadChangelog().Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## "))
                list.Children.Add(new TextBlock
                {
                    Text = line[3..],
                    FontSize = 16,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, list.Children.Count == 0 ? 0 : 12, 0, 2)
                });
            else if (line.StartsWith("- "))
                list.Children.Add(Bullet(line[2..]));
        }

        var close = new Button { Content = "Close", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = true, IsCancel = true };
        var dialog = new Window
        {
            Title = "What's new in Auto Clicker",
            Icon = owner.Icon,
            Width = 560,
            Height = 600,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel
            {
                Children =
                {
                    new Border
                    {
                        [DockPanel.DockProperty] = Dock.Bottom,
                        Padding = new Thickness(16, 10),
                        Child = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Children = { close } }
                    },
                    new ScrollViewer { Content = list }
                }
            }
        };
        close.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    static Control Bullet(string text)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("16,*") };
        row.Children.Add(new TextBlock { Text = "•", Opacity = 0.7 });
        var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    static string ReadChangelog()
    {
        try
        {
            using var stream = typeof(ChangelogWindow).Assembly.GetManifestResourceStream("CHANGELOG.md");
            if (stream != null) return new StreamReader(stream).ReadToEnd().Replace("\r", "");
        }
        catch (Exception) { /* fall through */ }
        return "## Couldn't load the list of changes\n- See the releases page on GitHub instead.";
    }
}
