using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Cockpit.App.Views;

namespace Cockpit.App;

/// <summary>
/// Headless startup mode that renders a window off-screen via the Avalonia Skia headless platform and
/// writes a single frame to disk as PNG. Lets an external caller verify the UI layout without a display
/// attached (Iron Law #9: automated visual verification). <paramref name="scene"/> picks which window:
/// the main cockpit by default, or a dialog whose layout would otherwise be unverifiable.
/// </summary>
internal static class Screenshotter
{
    private const int DefaultWindowWidth = 1100;
    private const int DefaultWindowHeight = 760;

    public static void Run(string outputPngPath, int width = DefaultWindowWidth, int height = DefaultWindowHeight, string? scene = null)
    {
        BuildHeadlessAvaloniaApp().SetupWithoutStarting();

        Window window = scene switch
        {
            "about" => new AboutDialog { DataContext = ViewModels.AboutInfo.FromAssembly(typeof(Screenshotter).Assembly) },
            "options" => new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() },
            "shortcuts" => _OptionsOnTab("Shortcuts"),
            _ => new MainWindow { DataContext = new ViewModels.CockpitViewModel() },
        };

        // A SizeToContent dialog measures itself; only the main window takes the requested size.
        if (window is MainWindow)
        {
            window.Width = width;
            window.Height = height;
        }

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

    // Renders the Options dialog with one of its tabs selected, so a tab other than the first one can be
    // verified without a display.
    private static OptionsDialog _OptionsOnTab(string header)
    {
        var dialog = new OptionsDialog { DataContext = new ViewModels.CockpitViewModel() };
        var tabs = dialog.FindControl<TabControl>("Tabs")
            ?? throw new InvalidOperationException("The Options dialog has no 'Tabs' TabControl to select on.");

        tabs.SelectedItem = tabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(tab => string.Equals(tab.Header as string, header, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"The Options dialog has no '{header}' tab.");

        return dialog;
    }

    private static AppBuilder BuildHeadlessAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .With(Program.CockpitFontOptions())
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                UseHeadlessDrawing = false,
            });
}
