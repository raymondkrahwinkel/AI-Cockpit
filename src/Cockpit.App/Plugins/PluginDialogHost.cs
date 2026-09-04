using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.App.Services;
using Cockpit.Core.Abstractions;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// #14: shows a plugin's content in a window beside the cockpit, wrapped in `CockpitWindowChrome`. AC-367:
// these are surfaces, not questions, so unlike a modal they never take every running session down.
// Reduced to one window per plugin-supplied key, never per caption — a caption is not an identity.
internal sealed class PluginDialogHost(SurfaceWindows surfaces) : IPluginDialogHost, ISingletonService
{
    public async Task ShowDialogAsync(string title, Func<Control> createContent, double width, double height, Func<Task>? onOpenSettings = null, string? singleInstanceKey = null)
    {
        var key = _Key(singleInstanceKey);
        if (surfaces.TryActivateAsync(key) is { } open)
        {
            await open;
            return;
        }

        if (!_TryCreateWindow(title, width, height, out var window, out var owner, out _))
        {
            return;
        }

        window.Content = _WithToasts(createContent(), owner);
        CockpitWindowChrome.Apply(window, title, onSettings: onOpenSettings is null ? null : () => _ = onOpenSettings());
        await surfaces.ShowAsync(key, window, owner);
    }

    public async Task ShowSettingsDialogAsync(string title, Func<Control> createView, double width, double height, Action? onSaved = null, string? singleInstanceKey = null)
    {
        var key = _Key(singleInstanceKey);
        if (surfaces.TryActivateAsync(key) is { } open)
        {
            await open;
            return;
        }

        if (!_TryCreateWindow(title, width, height, out var window, out var owner, out var maximum))
        {
            return;
        }

        var view = createView();
        var footer = BuildSettingsFooter(window, view, onSaved);
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
        await surfaces.ShowAsync(key, window, owner);
    }

    // The Save/Close footer every plugin settings dialog gets, extracted so its failure branch is testable
    // without `ApplicationLifetime`. AC-1003: the immediate half of the staged contract — a standalone
    // window stages and commits on the same click, and the refusal reason now comes from the view itself.
    internal static Border BuildSettingsFooter(Window window, Control view, Action? onSaved)
    {
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

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
                // Through the same staging the Options screen uses (AC-1004), for the settings-saved signal rather
                // than for the holding: staging it here and committing on the next line is what pins that signal to
                // the write for both hosts at once, instead of each host remembering to fire it in the right order.
                var staging = new PluginSettingsStaging();
                // The tag is unread here — this window hosts one view, so only the reason below is ever shown.
                if (!staging.TryStage(settingsView, view.GetType().Name, onSaved, out var error))
                {
                    status.Text = error;
                    status.IsVisible = true;
                    return;
                }

                // AC-479: a write that threw keeps the window open with its reason, like a refusal — closing on it
                // would report a save that did not happen.
                if (staging.Commit() is [var failure, ..])
                {
                    status.Text = failure.Reason;
                    status.IsVisible = true;
                    return;
                }

                window.Close();
            };
            buttons.Children.Add(save);
        }

        var content = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        content.Children.Add(buttons);
        content.Children.Add(status);

        // The same band the window's title bar sits on (AC-335), not the rail colour it used to use: the two are
        // the top and bottom edge of one window, and a footer a shade darker than its own header reads as a
        // different surface stuck to the bottom.
        return new Border
        {
            Padding = new Thickness(14, 12),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = _Brush("CockpitChromeBgBrush"),
            BorderBrush = _Brush("CockpitHairlineSoftBrush"),
            Child = content,
        };
    }

    // No key means no folding: a fresh object matches nothing, so every ask opens its own window. Keys are
    // namespaced away from the cockpit's own surfaces, which key on their dialog's Type, so a plugin cannot
    // collide with Options however it names its window.
    private static object _Key(string? singleInstanceKey) =>
        singleInstanceKey is null ? new object() : ("plugin", singleInstanceKey);

    // The cockpit's toasts live on the main window, so a toast raised from inside a plugin's window (a workflow's
    // Notify step, say) appeared nowhere at all when that window covered it. The same overlay goes on top of this
    // one, bound to the same view model, so one toast shows in whichever window the operator is looking at.
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

        // Owner is whichever window is actually active, not always the main one: a settings dialog opened
        // from a plugin dialog's gear must sit on top of that dialog, not open behind it via the main window.
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
