using System.Diagnostics;
using Cockpit.App.Diagnostics;

namespace Cockpit.App.ViewTests;

[Collection("avalonia")]
public class OptionsOpenMeasurementTests
{
    [Fact]
    public async Task LocalProbe() => await HeadlessAvalonia.RunAsync(async () =>
    {
        var logger = new ProbeLogger();
        var vm = new Cockpit.App.ViewModels.CockpitViewModel();
        typeof(Cockpit.App.ViewModels.CockpitViewModel).GetField("_logger", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(vm, logger);
        var delay = 0;
        ((Cockpit.App.Plugins.IPluginContributionSink)vm).AddPluginSettings("probe", "Probe", () => new ProbeView(() => delay));
        vm.BeginOptionsEdit();
        vm.PluginOptionsRows.Single().EnsureContent();
        var dialog = new Cockpit.App.Views.OptionsDialog { DataContext = vm };
        dialog.Show();
        dialog.UpdateLayout();
        var buttons = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(dialog).OfType<Avalonia.Controls.Button>().ToArray();
        buttons.Single(button => button.Content as string == "Apply").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await (vm.ApplyOptionsAndStayCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(ExpectedNormalCount, logger.Lines.Count);
        Assert.True(dialog.IsVisible);
        logger.Lines.Clear();
        buttons.Single(button => button.Content as string == "Apply and Close").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await (vm.ApplyOptionsCommand.ExecutionTask ?? Task.CompletedTask);
        Assert.Equal(ExpectedNormalCount * 2, logger.Lines.Count);
        Assert.False(dialog.IsVisible);
        logger.Lines.Clear();
        vm.BeginOptionsEdit();
        vm.PluginOptionsRows.Single().EnsureContent();
        var discard = new Cockpit.App.Views.OptionsDialog { DataContext = vm };
        discard.Show();
        discard.Close();
        Assert.Equal(ExpectedNormalCount, logger.Lines.Count);
        Assert.False(discard.IsVisible);
        logger.Lines.Clear();
        vm.BeginOptionsEdit();
        vm.PluginOptionsRows.Single().EnsureContent();
        delay = 950;
        await vm.ApplyOptionsAndStayCommand.ExecuteAsync(null);
        Assert.Single(logger.Lines);
        Assert.Contains("plugin-probe:", logger.Lines.Single());
        logger.Lines.Clear();
        var slowClose = new Cockpit.App.Views.OptionsDialog { DataContext = vm };
        slowClose.Show();
        slowClose.Closing += (_, _) => Thread.Sleep(950);
        slowClose.Close();
        Assert.Single(logger.Lines);
        Assert.Contains("options close slow", logger.Lines.Single());
    });

    private static int ExpectedNormalCount => int.Parse(Environment.GetEnvironmentVariable("AC1906_EXPECT_NORMAL") ?? "0");

    private sealed class ProbeView(Func<int> delay) : Avalonia.Controls.TextBlock, Cockpit.Plugins.Abstractions.IPluginSettingsView
    {
        public bool TryStage(out Action? commit, out string? error)
        {
            commit = () => Thread.Sleep(delay());
            error = null;
            return true;
        }
    }

    private sealed class ProbeLogger : Microsoft.Extensions.Logging.ILogger<Cockpit.App.ViewModels.CockpitViewModel>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            Lines.Add(line);
            File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("COCKPIT_STATE_ROOT") ?? throw new InvalidOperationException("Isolated state required"), "cockpit.log"), $"{level} {line}{Environment.NewLine}");
        }
    }

    [Fact]
    public void Finish_ReportsOnlyAtTheThreshold_WithTotalAndPhases()
    {
        long now = 0;
        var measurement = new OptionsMeasurement("open", () => now);
        measurement.Mark("plugins");
        now = Stopwatch.Frequency * 849 / 1000;

        Assert.Null(measurement.Finish());

        long slowNow = 0;
        var slowMeasurement = new OptionsMeasurement("open", () => slowNow);
        slowNow = Stopwatch.Frequency * 3 / 10;
        slowMeasurement.Mark("plugins");
        slowNow = Stopwatch.Frequency * 85 / 100;
        slowMeasurement.Mark("presented");

        Assert.Equal("options open slow total=850ms phases=plugins:300,presented:850", slowMeasurement.Finish());

        var apply = new OptionsMeasurement("apply", () => now);
        var close = new OptionsMeasurement("close", () => now);
        Assert.Null(apply.Finish());
        Assert.Null(close.Finish());
        now += Stopwatch.Frequency * 85 / 100;
        apply.Mark("config");
        close.Mark("closed");
        Assert.Equal("options apply slow total=850ms phases=config:850", apply.Finish());
        Assert.Equal("options close slow total=850ms phases=closed:850", close.Finish());
    }
}
