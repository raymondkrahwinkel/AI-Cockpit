using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Cockpit.App.Controls;

namespace Cockpit.App.Views;

/// <summary>
/// The gate behind <c>ScreenshotSelectionViewModel.Confirm()</c> (AC-566): shows exactly the bytes that would be
/// injected, and asks before they are. Whatever is passed in is drawn as-is — nothing here re-encodes or crops
/// it, so what is approved is what gets sent.
/// </summary>
public partial class ScreenshotPreviewWindow : Window
{
    private Bitmap? _bitmap;

    public ScreenshotPreviewWindow()
    {
        InitializeComponent();
        // Escape closes with no result, which Close() without one already gives — the operator's own decline.
        CockpitWindowChrome.Apply(this, "Preview");
    }

    /// <summary>
    /// Shows the preview and waits for the operator: true to send exactly these bytes, false to go back to the
    /// selection window — whose region and marks this never touches.
    /// </summary>
    public static async Task<bool> ShowAsync(byte[] png, string destination, Window owner) =>
        await Build(png, destination).ShowDialog<bool>(owner);

    /// <summary>
    /// The window built and wired, without being put on screen — the render harness's own step, the same split
    /// <see cref="ScreenshotSelectionWindow.Build"/> already makes for the surface this one gates.
    /// </summary>
    internal static ScreenshotPreviewWindow Build(byte[] png, string destination)
    {
        using var stream = new MemoryStream(png);
        var bitmap = new Bitmap(stream);

        var window = new ScreenshotPreviewWindow { _bitmap = bitmap };
        window.Preview.Source = bitmap;
        window.DestinationText.Text = $"Goes to {destination}.";

        return window;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void OnSend(object? sender, RoutedEventArgs e) => Close(true);

    private void OnBack(object? sender, RoutedEventArgs e) => Close(false);
}
