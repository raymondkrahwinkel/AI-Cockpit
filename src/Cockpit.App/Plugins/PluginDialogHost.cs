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
    public async Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null)
    {
        if (!_TryCreateWindow(title, width, height, out var window, out var owner, out _))
        {
            return;
        }

        window.Content = _WithToasts(createContent(), owner);
        CockpitWindowChrome.Apply(window, title, onSettings: onOpenSettings is null ? null : () => _ = onOpenSettings());
        await window.ShowDialog(owner);
    }

    public async Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null)
    {
        if (!_TryCreateWindow(title, width, height, out var window, out var owner, out var maximum))
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

        // The same band the window's title bar sits on (AC-335), not the rail colour it used to use: the two are
        // the top and bottom edge of one window, and a footer a shade darker than its own header reads as a
        // different surface stuck to the bottom.
        var footer = new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = _Brush("CockpitChromeBgBrush"),
            BorderBrush = _Brush("CockpitHairlineSoftBrush"),
            Child = buttons,
        };
        DockPanel.SetDock(footer, Dock.Bottom);

        var root = new DockPanel();
        root.Children.Add(footer);

        // A view that declares sections is drawn with the Options navigation rail beside it (AC-316), and the
        // dialog opens that much wider so the settings keep the room they had — up to the cockpit's own cap.
        var body = PluginSettingsBodyBuilder.Build(view);
        if (body.HasRail)
        {
            (window.Width, window.MinWidth) =
                PluginSettingsBodyBuilder.GrowForRail(window.Width, window.MinWidth, maximum.Width, _RailWidth());
        }

        root.Children.Add(body.Content);
        window.Content = _WithToasts(root, owner);

        CockpitWindowChrome.Apply(window, title);
        await window.ShowDialog(owner);
    }

    // A dialog is modal, and the cockpit's toasts live on the window behind it — so a toast raised from inside a
    // plugin's dialog (a workflow's Notify step, say) appeared nowhere at all. The same overlay goes on top of the
    // dialog, bound to the same view model, so one toast shows in whichever window the operator is looking at.
    private static Control _WithToasts(Control content, Window owner)
    {
        var overlay = new ToastOverlay { DataContext = owner.DataContext };

        return new Panel { Children = { content, overlay } };
    }

    private static bool _TryCreateWindow(string title, double width, double height, out Window window, out Window owner, out Size maximum)
    {
        window = null!;
        owner = null!;
        maximum = default;
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main } lifetime)
        {
            return false;
        }

        // The owner is whichever window the operator is actually looking at, not always the main one: a settings
        // dialog opened from the gear on a plugin's own dialog must sit on top of that dialog. Owned by the main
        // window it would open behind a modal that blocks its own owner — visible nowhere, with the app looking
        // hung. The main window stays the fallback, which is what it is for every dialog opened from the cockpit
        // itself.
        owner = lifetime.Windows.LastOrDefault(candidate => candidate.IsActive) ?? main;

        // The size a plugin asks for is a wish, not a law: a dialog that wants 1400px on a 1280px-wide cockpit
        // opens with its content cut off, which is how a canvas ends up cropped. Fit it to the cockpit window —
        // the main one, whichever window it is centred over — and let the operator resize from there.
        maximum = new Size(main.Width * 0.94, main.Height * 0.94);

        window = new Window
        {
            Title = title,
            Width = Math.Min(width, maximum.Width),
            Height = Math.Min(height, maximum.Height),
            MinWidth = Math.Min(720, maximum.Width),
            MinHeight = Math.Min(480, maximum.Height),
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        return true;
    }

    // The width Theme.axaml draws the rail at. Read rather than repeated: the dialog has to be that much wider
    // before the rail exists to be measured, and the style resolves the same key — so a theme without it draws no
    // rail width and grows the dialog by none either.
    private static double _RailWidth() =>
        Application.Current?.TryFindResource("CockpitSubnavRailWidth", out var value) == true && value is double width ? width : 0;

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
