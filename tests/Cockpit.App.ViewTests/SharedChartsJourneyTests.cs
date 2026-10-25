using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Cockpit.Plugin.Shared.UI;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class SharedChartsJourneyTests
{
    [Fact]
    public void SharedChartControls_AreAvailableToTheViewJourney()
    {
        var assembly = typeof(SharedChartsJourneyTests).Assembly;

        Assert.NotNull(assembly.GetType("Cockpit.Plugin.Shared.UI.StackedBarChartControl"));
        Assert.NotNull(assembly.GetType("Cockpit.Plugin.Shared.UI.GroupedBarChartControl"));
        Assert.NotNull(assembly.GetType("Cockpit.Plugin.Shared.UI.HorizontalBarChartControl"));
        Assert.NotNull(assembly.GetType("Cockpit.Plugin.Shared.UI.StatTile"));
        Assert.NotNull(assembly.GetType("Cockpit.Plugin.Shared.UI.RangePicker"));
    }

    [Fact]
    public Task FixedDataAndEmptyData_RenderInBothThemes() => HeadlessAvalonia.RunAsync(() =>
    {
        var screen = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackedBarChartControl { Height = 90, Items = [new("Mon", [new("Own", 3, Color.FromRgb(76, 139, 245)), new("Rule", 2, Color.FromRgb(61, 190, 139), true)])] },
                new GroupedBarChartControl { Height = 90, Items = [new("Mon", 3, 2)] },
                new HorizontalBarChartControl { Height = 32, Segments = [new("Project", 3, Color.FromRgb(76, 139, 245)), new("Rules", 2, Color.FromRgb(61, 190, 139), true)] },
                new StatTile { Height = 70, Label = "Your time", Value = "4h 30", Detail = "Today" },
                new RangePicker { Height = 32, Ranges = ["Hour", "Day", "Workweek", "Week", "Month", "Custom range"], SelectedRange = "Workweek" },
                new StackedBarChartControl { Height = 60, EmptyText = "No data" },
            },
        };
        using var scene = RenderedScene.Show(screen, 720, 520);
        _Render(scene.Window, "shared-charts");
        Assert.NotNull(scene.Window.CaptureRenderedFrame());
        return Task.CompletedTask;
    });

    private static void _Render(Window window, string scene)
    {
        if (Environment.GetEnvironmentVariable("CHARTS_RENDER_DIR") is not { Length: > 0 } folder || Application.Current is not { } app)
        {
            return;
        }

        Directory.CreateDirectory(folder);
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            app.RequestedThemeVariant = theme;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame to save.");
            frame.Save(Path.Combine(folder, $"{scene}-{theme}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        app.RequestedThemeVariant = ThemeVariant.Default;
    }
}
