using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram.Wireframe;

// AC-873's quick-start, same one screen as DiagramQuickStartDialog (AC-816): name (prefilled, Enter is enough) and,
// optionally, the active session to couple.
internal static class WireframeQuickStartDialog
{
    public static async Task<WireframeQuickStart?> ShowAsync(ICockpitHost host, string defaultName)
    {
        WireframeQuickStart? result = null;

        await host.ShowDialogAsync("Nieuw wireframe", () =>
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
                Content = activePaneId is null ? "Geen actieve sessie om te koppelen" : $"Koppel aan sessie · {sessionLabel}",
                IsEnabled = activePaneId is not null,
            };

            void Confirm()
            {
                var name = string.IsNullOrWhiteSpace(nameBox.Text) ? defaultName : nameBox.Text.Trim();
                result = new WireframeQuickStart(name, coupleSession.IsChecked == true ? activePaneId : null);
            }

            var open = new Button { Content = "Openen", Classes = { "Accent" }, HorizontalAlignment = HorizontalAlignment.Right };
            open.Click += (sender, _) =>
            {
                Confirm();
                (sender as Control)?.FindAncestorOfType<Window>()?.Close();
            };
            var cancel = new Button
            {
                Content = "Annuleren",
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
                BorderBrush = _Brush("CockpitHairlineBrush"),
                [DockPanel.DockProperty] = Dock.Bottom,
                Child = new DockPanel { LastChildFill = false, Children = { open, cancel } },
            };

            var body = new StackPanel
            {
                Margin = new Thickness(16, 14),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Naam", FontSize = 11, Foreground = _Brush("CockpitTextSecondaryBrush") },
                    nameBox,
                    coupleSession,
                },
            };

            return new DockPanel { LastChildFill = true, Children = { footer, body } };
        }, "wireframe.quickstart", width: 420, height: 240);

        return result;
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
