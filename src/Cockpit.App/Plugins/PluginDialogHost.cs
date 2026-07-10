using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

/// <summary>
/// Shows a plugin's content in a modal window over the cockpit's main window (#14) with cockpit-styled
/// custom chrome — a hairline title bar (draggable, with a close button) instead of the OS title bar — so a
/// plugin dialog looks native to the app while staying resizable. The plugin owns the content control. The
/// settings variant adds a host-provided Save/Close footer so every plugin's settings dialog behaves the
/// same — Save calls the view's <see cref="IPluginSettingsView.Save"/> and closes the dialog on success.
/// </summary>
internal sealed class PluginDialogHost : IPluginDialogHost, ISingletonService
{
    public async Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height)
    {
        if (!_TryCreateWindow(title, width, height, out var window, out var owner))
        {
            return;
        }

        window.Content = _ChromeRoot(window, title, createContent(), footer: null);
        await window.ShowDialog(owner);
    }

    public async Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height)
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

        window.Content = _ChromeRoot(window, title, new ScrollViewer { Content = view }, footer);
        await window.ShowDialog(owner);
    }

    // Wraps the plugin body in the cockpit's custom chrome: a draggable title bar on top, the body filling,
    // and an optional footer docked at the bottom (settings Save/Close).
    private static Control _ChromeRoot(Window window, string title, Control body, Border? footer)
    {
        var root = new DockPanel();
        var titleBar = _TitleBar(window, title);
        DockPanel.SetDock(titleBar, Dock.Top);
        root.Children.Add(titleBar);

        if (footer is not null)
        {
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);
        }

        root.Children.Add(body);
        return root;
    }

    private static Control _TitleBar(Window window, string title)
    {
        var titleText = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0),
        };

        var closeButton = new Button
        {
            Content = "✕",
            Classes = { "Subtle" },
            FontSize = 13,
            Padding = new Thickness(12, 0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        closeButton.Click += (_, _) => window.Close();

        var bar = new DockPanel { Height = 38 };
        DockPanel.SetDock(closeButton, Dock.Right);
        bar.Children.Add(closeButton);
        bar.Children.Add(titleText);

        var wrapper = new Border
        {
            Background = _Brush("CockpitSecondaryBgBrush"),
            BorderBrush = _Brush("CockpitHairlineBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };

        // Drag the window by the title bar, but not when the press lands on the close button.
        wrapper.PointerPressed += (_, e) =>
        {
            if (e.Source is not Button)
            {
                window.BeginMoveDrag(e);
            }
        };

        return wrapper;
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
            Background = _Brush("CockpitPanelBgBrush"),
            // Cockpit-styled chrome: extend the client area under the title bar and draw our own, keeping
            // the OS resize borders.
            ExtendClientAreaToDecorationsHint = true,
            ExtendClientAreaTitleBarHeightHint = -1,
        };
        return true;
    }

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
