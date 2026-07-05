using Avalonia;
using Avalonia.Headless;
using Cockpit.App.Views;

namespace Cockpit.App;

/// <summary>
/// Headless startup mode that renders <see cref="MainWindow"/> off-screen via the Avalonia Skia
/// headless platform and writes a single frame to disk as PNG. Lets an external caller verify the
/// UI layout without a display attached (Iron Law #9: automated visual verification).
/// </summary>
internal static class Screenshotter
{
    private const int WindowWidth = 1100;
    private const int WindowHeight = 760;

    public static void Run(string outputPngPath)
    {
        BuildHeadlessAvaloniaApp().SetupWithoutStarting();

        var window = new MainWindow
        {
            DataContext = new ViewModels.CockpitViewModel(),
            Width = WindowWidth,
            Height = WindowHeight,
        };
        window.Show();

        var frame = window.CaptureRenderedFrame()
            ?? throw new InvalidOperationException("Headless renderer produced no frame to capture.");

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPngPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        frame.Save(outputPngPath);
        window.Close();
    }

    private static AppBuilder BuildHeadlessAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
