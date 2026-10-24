using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Plugin.Schedules.Contracts;

namespace Cockpit.Plugin.Schedules.UI;

// The small building blocks the four tabs share, on the host's theme tokens and style classes (PLUGIN-SDK §Theme
// tokens), so the screen follows Dark and Light and every other plugin's controls.
internal static class Ui
{
    public static TextBlock Title(string text) => new() { Text = text, FontSize = 16, FontWeight = FontWeight.SemiBold };

    public static TextBlock Hint(string text) =>
        new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "CockpitTextFaintBrush");

    public static TextBlock Muted(string text) =>
        new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }.Themed(TextBlock.ForegroundProperty, "CockpitTextSecondaryBrush");

    // A host theme resource, followed live, so a switch between Dark and Light repaints without reopening the screen.
    public static T Themed<T>(this T control, AvaloniaProperty property, string key) where T : Control
    {
        control.Bind(property, control.GetResourceObservable(key));
        return control;
    }

    public static TextBlock Section(string text) => new()
    {
        Text = text,
        Classes = { "optionsSectionHeader" },
        Margin = new Thickness(0, 14, 0, 6),
    };

    public static Button Button(string text, Action onClick, params string[] classes)
    {
        var button = new Button { Content = text };
        foreach (var name in classes)
        {
            button.Classes.Add(name);
        }

        button.Click += (_, _) => onClick();
        return button;
    }

    // A click cannot await, so the work is awaited in the handler and a failure is reported rather than lost.
    public static Button AsyncButton(string text, Func<Task> onClick, Action<Exception> report, params string[] classes)
    {
        var button = new Button { Content = text };
        foreach (var name in classes)
        {
            button.Classes.Add(name);
        }

        button.Click += async (_, _) =>
        {
            try
            {
                await onClick();
            }
            catch (Exception exception)
            {
                report(exception);
            }
        };
        return button;
    }

    // An outcome as the mockup's pill: Running…, Succeeded, Failed, Nothing to deliver; a task that never ran says so.
    public static Control Pill(RunRecord? run)
    {
        if (run is null)
        {
            return Hint("Never ran");
        }

        var (text, brush) = run.Outcome switch
        {
            RunOutcome.Running => ("Running…", "CockpitStatusBusyBrush"),
            RunOutcome.Succeeded => ("Succeeded", "CockpitStatusDoneBrush"),
            RunOutcome.Failed => ("Failed", "CockpitStatusErrorBrush"),
            RunOutcome.NothingToDeliver => ("Nothing to deliver", "CockpitTextSecondaryBrush"),
            RunOutcome.Skipped => ("Skipped", "CockpitStatusWaitingBrush"),
            _ => ("Missed", "CockpitTextFaintBrush"),
        };
        return Tag(text, brush);
    }

    public static Border Tag(string text, string brushKey = "CockpitTextSecondaryBrush") => new()
    {
        Classes = { "tag" },
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 11 }.Themed(TextBlock.ForegroundProperty, brushKey),
    };

    // A label and its value per row, as the mockup's detail panels.
    public static Grid KeyValues(params (string Key, Control Value)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*"), RowSpacing = 6 };
        for (var index = 0; index < rows.Length; index++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = Muted(rows[index].Key);
            Grid.SetRow(key, index);
            grid.Children.Add(key);
            Grid.SetRow(rows[index].Value, index);
            Grid.SetColumn(rows[index].Value, 1);
            grid.Children.Add(rows[index].Value);
        }

        return grid;
    }

    public static TextBlock Value(string text, bool mono = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = mono ? new FontFamily("Cascadia Mono,Consolas,monospace") : FontFamily.Default,
    };

    // A segmented choice (All · Enabled · …): one picked, as the mockup's .seg.
    public static StackPanel Segments(IReadOnlyList<(string Key, string Label)> options, string selected, Action<string> pick)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (key, label) in options)
        {
            var button = new ToggleButton { Content = label, IsChecked = key == selected, Classes = { "Compact" } };
            button.Click += (_, _) => pick(key);
            panel.Children.Add(button);
        }

        return panel;
    }

    public static Border Card(Control child, double padding = 14) =>
        new Border { Child = child, Padding = new Thickness(padding), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) }
            .Themed(Border.BorderBrushProperty, "CockpitHairlineBrush")
            .Themed(Border.BackgroundProperty, "CockpitPanelBgBrush");

    // A warning or an explanation inside a form, as the mockup's .warn and .info.
    public static Border Note(string text, bool warning) =>
        new Border
        {
            Padding = new Thickness(10, 8),
            Margin = new Thickness(0, 6, 0, 0),
            CornerRadius = new CornerRadius(6),
            Child = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12 },
        }.Themed(Border.BackgroundProperty, warning ? "CockpitStatusWaitingChipTintBrush" : "CockpitInsetBgBrush");

    public static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    public static string When(DateTimeOffset? at) =>
        at is { } moment ? moment.ToLocalTime().ToString("ddd d MMM HH:mm", System.Globalization.CultureInfo.InvariantCulture) : "—";

    public static string Duration(TimeSpan duration) =>
        duration.TotalMinutes >= 1 ? $"{(int)duration.TotalMinutes}m {duration.Seconds:00}s" : $"{duration.Seconds}s";

    public static string Trigger(RunTrigger trigger) => trigger switch
    {
        RunTrigger.Schedule => "Schedule",
        RunTrigger.CatchUp => "Catch-up",
        RunTrigger.Manual => "Manual · by you",
        _ => "MCP · by an agent",
    };

    public static bool IsDangerous(ScheduledTask task) =>
        task.Action.Kind == TaskActionKind.AgentPrompt && task.Action.Permission == AgentPermission.Write;
}

// A dialog drawn over the screen itself, as the mockup's modal: a card on a scrim, closed by its own buttons.
internal sealed class Overlay : Panel
{
    public Overlay()
    {
        IsVisible = false;
        this.Themed(BackgroundProperty, "CockpitScrimBrush");
    }

    public void Show(string title, Control body, params Control[] buttons)
    {
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        foreach (var button in buttons)
        {
            footer.Children.Add(button);
        }

        Children.Clear();
        Children.Add(new Border
        {
            Width = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(18),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = new StackPanel { Spacing = 10, Children = { Ui.Title(title), body, footer } },
        }.Themed(Border.BorderBrushProperty, "CockpitHairlineBrush").Themed(Border.BackgroundProperty, "CockpitFloatingCardBrush"));
        IsVisible = true;
    }

    public void Close()
    {
        IsVisible = false;
        Children.Clear();
    }
}
