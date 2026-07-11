using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Shows a plugin's content in a modal window over the cockpit's main window (#14), wrapped in the shared
/// cockpit window chrome (<see cref="CockpitWindowChrome"/>) so a plugin dialog looks native to the app.
/// The plugin owns the content control. The settings variant adds a host-provided Save/Close footer so
/// every plugin's settings dialog behaves the same — Save calls the view's <see cref="IPluginSettingsView.Save"/>
/// and closes the dialog on success.
/// </summary>
internal sealed class PluginDialogHost : IPluginDialogHost, ISingletonService
{
    public async Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height)
    {
        if (!_TryCreateWindow(title, width, height, out var window, out var owner))
        {
            return;
        }

        window.Content = createContent();
        CockpitWindowChrome.Apply(window, title);
        await window.ShowDialog(owner);
    }

    public async Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null)
    {
        if (!_TryCreateWindow(title, width, height, out var window, out var owner))
        {
            return;
        }

        var view = createView();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => window.Close();
        buttons.Children.Add(close);

        // The Save button appears only when the view opts in; it persists via the view and closes on success.
        if (view is IPluginSettingsView settingsView)
        {
            var save = new Button { Content = "Save", Classes = { "Accent" } };
            save.Click += (_, _) =>
            {
                if (settingsView.Save())
                {
                    onSaved?.Invoke();
                    window.Close();
                }
            };
            buttons.Children.Add(save);
        }

        var footer = new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            Child = buttons,
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = view });
        window.Content = root;

        CockpitWindowChrome.Apply(window, title);
        await window.ShowDialog(owner);
    }

    private static bool _TryCreateWindow(string title, double width, double height, out Window window, out Window owner)
    {
        window = null!;
        owner = null!;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            return false;
        }

        owner = main;
        window = new Window
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return true;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
