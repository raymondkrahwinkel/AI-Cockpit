using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Views;

/// <summary>
/// The plugin store dialog (#62): sidebar/search/sort/grid/detail over a
/// <see cref="PluginStoreDialogViewModel"/>. Disposes the view model on close so it unsubscribes from
/// the shared (long-lived) <see cref="PluginManagerViewModel"/>'s collection/property-changed events —
/// otherwise every store-dialog open would leak one more subscription on that shared instance.
/// </summary>
public partial class PluginStoreDialog : Window
{
    /// <summary>How much of the screen's working area the dialog may take when its designed size does not fit.</summary>
    private const double MaxScreenFraction = 0.9;

    public PluginStoreDialog()
    {
        InitializeComponent();
        CockpitWindowChrome.Apply(this);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>
    /// Shrinks the dialog to fit when its designed size does not. It is sized for a desktop (a catalogue grid
    /// beside a detail panel needs the room), and a fixed size larger than the screen is not a bigger dialog —
    /// it is one whose buttons are past the bottom edge, centred on its owner with nothing to drag it back by.
    /// </summary>
    private void OnOpened(object? sender, EventArgs e)
    {
        if (Screens.ScreenFromWindow(this) is not { } screen)
        {
            return;
        }

        // WorkingArea is in physical pixels and Width/Height are in DIPs, so the scaling has to come out first
        // or this clamps to the wrong number on any display that is not at 100%.
        var available = screen.WorkingArea;
        var maxWidth = available.Width / screen.Scaling * MaxScreenFraction;
        var maxHeight = available.Height / screen.Scaling * MaxScreenFraction;

        // Never below the minimums: a dialog too small to use is the failure this is avoiding, not a fix for it.
        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, maxWidth));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, maxHeight));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        (DataContext as PluginStoreDialogViewModel)?.Dispose();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnOpenHomepage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Homepage: { } url })
        {
            _OpenUrl(url);
        }
    }

    private void OnOpenRepository(object? sender, RoutedEventArgs e)
    {
        if (DataContext is PluginStoreDialogViewModel { SelectedPlugin.Repository: { } url })
        {
            _OpenUrl(url);
        }
    }

    // Mirrors AboutDialog/MarkdownView's link handler: only ever shell out to an http(s) URL, and a
    // failed browser launch must not crash the UI thread.
    private static void _OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best-effort: a failed browser launch must not crash the UI thread.
        }
    }
}
