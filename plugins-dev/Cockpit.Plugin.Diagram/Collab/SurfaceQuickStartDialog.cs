using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Collab;

// AC-816/AC-843/AC-873's counterpart: a working title and, optionally, the active session to couple. TemplateSource
// is the chosen template's source, or "" when this surface offers none (the whiteboard).
internal sealed record SurfaceQuickStart(string Name, string? SessionPaneId, string TemplateSource);

// AC-911: the one quick-start dialog behind New diagram/whiteboard/wireframe, replacing three identical classes
// down to their return record. What differs per surface is the title, the dialog key, whether "link session"
// starts checked, and the template list — empty for the whiteboard, which keeps its old 420x240, no-template look.
internal static class SurfaceQuickStartDialog
{
    public static async Task<SurfaceQuickStart?> ShowAsync(
        ICockpitHost host,
        string title,
        string dialogKey,
        string defaultName,
        bool linkSessionByDefault,
        IReadOnlyList<SurfaceTemplate> templates,
        Func<SurfaceTemplate, Control>? preview = null)
    {
        SurfaceQuickStart? result = null;
        var hasTemplates = templates.Count > 0;

        await host.ShowDialogAsync(title, () =>
        {
            var nameBox = new TextBox { Text = defaultName };
            nameBox.AttachedToVisualTree += (_, _) =>
            {
                nameBox.Focus();
                nameBox.SelectAll();
            };

            var activePaneId = host.Sessions.ActivePaneId;
            var sessionLabel = host.Sessions.ActiveSessionUsage?.ProfileLabel ?? activePaneId;
            var coupleSession = new CheckBox
            {
                Content = activePaneId is null ? "No active session to link" : $"Link to session · {sessionLabel}",
                IsEnabled = activePaneId is not null,
                IsChecked = activePaneId is not null && linkSessionByDefault,
            };

            Func<SurfaceTemplate>? selectedTemplate = null;
            Control? templateStrip = null;
            if (hasTemplates)
            {
                (templateStrip, selectedTemplate) = SurfaceTemplateStrip.Build(templates, preview!);
            }

            void Confirm()
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
                result = new SurfaceQuickStart(name, coupleSession.IsChecked == true ? activePaneId : null, selectedTemplate?.Invoke().Source ?? "");
            }

            var open = new Button { Content = "Open", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Right };
            open.Click += (sender, _) =>
            {
                Confirm();
                (sender as Control)?.FindAncestorOfType<Window>()?.Close();
            };
            var cancel = new Button
            {
                Content = "Cancel",
                Classes = { "Ghost" },
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            cancel.Click += (sender, _) => (sender as Control)?.FindAncestorOfType<Window>()?.Close();

            nameBox.KeyDown += (sender, e) =>
            {
                if (e.Key != Key.Enter)
                {
                    return;
                }

                Confirm();
                (sender as Control)?.FindAncestorOfType<Window>()?.Close();
            };

            var footer = new Border
            {
                Padding = new Thickness(14, 11),
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = SurfaceChrome.Brush("CockpitHairlineBrush"),
                [DockPanel.DockProperty] = Dock.Bottom,
                Child = new DockPanel { LastChildFill = false, Children = { open, cancel } },
            };

            var body = new StackPanel { Margin = new Thickness(16, 14), Spacing = 10 };
            body.Children.Add(new TextBlock { Text = "Name", FontSize = 11, Foreground = SurfaceChrome.Brush("CockpitTextSecondaryBrush") });
            body.Children.Add(nameBox);
            body.Children.Add(coupleSession);
            if (templateStrip is not null)
            {
                body.Children.Add(new TextBlock { Text = "Template", FontSize = 11, Foreground = SurfaceChrome.Brush("CockpitTextSecondaryBrush") });
                body.Children.Add(new ScrollViewer { MaxHeight = 260, Content = templateStrip });
            }

            return new DockPanel { LastChildFill = true, Children = { footer, body } };
        }, dialogKey, width: 460, height: hasTemplates ? 560 : 240);

        return result;
    }
}
