using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.App.Docking;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1259: eleven hours of production log held no line from the view layer, so the two things a freeze
/// investigation most wants on the timeline — a panel reparented, a terminal resized — could neither be
/// confirmed nor ruled out. Each now leaves exactly one line, and "exactly" is the assertion in both
/// directions: none is the failure these tests exist for, and one per layout pass is the other, since that
/// would flood the log and allocate inside the very path being measured.
/// </summary>
[Collection("avalonia")]
public sealed class Ac1259FreezeTraceTests
{
    [Fact]
    public async Task EachAssistantHostSwap_LeavesExactlyOneLine_AndAHostThatDidNotChangeLeavesNone()
    {
        await HeadlessAvalonia.RunAsync(() =>
        {
            var lines = new List<string>();
            var (coordinator, _, panels) = AssistantDockHostSwapTests.Build(
                new _CollectingLogger<AssistantIndicatorCoordinator>(lines));

            // Undocked to start with, so the sidebar chip puts the chat in its own window.
            coordinator.Indicator.ClickCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(lines);
            Assert.Contains("its own window", lines[0], StringComparison.Ordinal);

            // Clicking the chip again only brings that window forward. Nothing moved, so nothing is claimed to have.
            lines.Clear();
            coordinator.Indicator.ClickCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(lines);

            // The header's Dock button, then the rail building the panel — which is where the chat actually lands.
            var chat = (AssistantChatViewModel)coordinator.OpenChatWindow!.DataContext!;
            chat.ToggleDockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            _BuildTheRailPanel(panels);
            Assert.Single(lines);
            Assert.Contains("the dock rail", lines[0], StringComparison.Ordinal);

            // And back out again: the undock AC-1256's freeze followed within seconds.
            lines.Clear();
            chat.ToggleDockCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Single(lines);
            Assert.Contains("its own window", lines[0], StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// The route the header button never touches: docked already, the rail builds the panel by itself — a restart
    /// into a docked assistant, or a click on the rail tab. Logging at the button would leave both of those silent.
    /// </summary>
    [Fact]
    public async Task DockingWithoutTheHeaderButton_StillLeavesItsLine()
    {
        await HeadlessAvalonia.RunAsync(async () =>
        {
            var lines = new List<string>();
            var (_, cockpit, panels) = AssistantDockHostSwapTests.Build(
                new _CollectingLogger<AssistantIndicatorCoordinator>(lines));

            await cockpit.SetAssistantDockedAsync(true, AssistantIndicatorCoordinator.DockPanelId);
            _BuildTheRailPanel(panels);

            Assert.Single(lines);
            Assert.Contains("the dock rail", lines[0], StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// A drag on a pane splitter walks the terminal through several grids before it lands on one. The log gets the
    /// grid it landed on, once, with how many it walked through — not one line for each of them.
    /// </summary>
    [Fact]
    public async Task ATerminalResize_LeavesOneLine_HoweverManyGridsItPassedThroughOnTheWay()
    {
        var lines = new List<string>();
        var previousServices = Program.Services;
        var container = new ServiceCollection()
            .AddLogging(builder => builder.AddProvider(new _CollectingLoggerProvider(lines)))
            .BuildServiceProvider();

        try
        {
            Program.Services = container;

            await HeadlessAvalonia.RunAsync(async () =>
            {
                // Built after Program.Services is standing: the view reads its logger out of the container in a
                // field initialiser, the same service-locator lookup it does under the real app's view locator.
                var view = new TtyView { DataContext = new TtyViewModel() };
                var window = new Window { Width = 900, Height = 600, Content = view };
                window.Show();

                List<string> fromTheWindow;
                List<string> fromTheBurst;
                try
                {
                    window.UpdateLayout();
                    await _AfterTheSettleWindow();
                    fromTheWindow = _Settled(lines);

                    // Then the burst a drag on a real compositor produces: several grids inside one settle
                    // window. A headless terminal coalesces a resized window into a single Resized, so the
                    // sizes are handed to the view's own handler — which is the code under test either way.
                    lines.Clear();
                    foreach (var rows in new[] { 30, 26, 22, 18 })
                    {
                        view.OnTerminalResized(null, (110, rows));
                    }

                    await _AfterTheSettleWindow();
                    fromTheBurst = _Settled(lines);
                }
                finally
                {
                    window.Close();
                }

                // A real layout, through the real terminal, resolving the logger the way the app does.
                Assert.True(fromTheWindow.Count == 1,
                    $"laying the pane out left {fromTheWindow.Count} settled line(s), not one:"
                    + Environment.NewLine + string.Join(Environment.NewLine, fromTheWindow));

                Assert.True(fromTheBurst.Count == 1,
                    $"a four-grid burst left {fromTheBurst.Count} settled line(s), not one:"
                    + Environment.NewLine + string.Join(Environment.NewLine, fromTheBurst));

                // And the grids it passed through are still countable from that one line.
                Assert.Contains("18 after 4 step(s)", fromTheBurst[0], StringComparison.Ordinal);
            });
        }
        finally
        {
            Program.Services = previousServices;
            await container.DisposeAsync();
        }
    }

    /// <summary>The settle timer's own interval is 150ms; this waits well past it and lets the dispatcher run.</summary>
    private static Task _AfterTheSettleWindow() => Task.Delay(600);

    private static List<string> _Settled(List<string> lines) =>
        [.. lines.Where(line => line.Contains("TTY resize settled", StringComparison.Ordinal))];


    // What the dock rail does with the registration, standing in for CockpitView's own _RebuildDockPanelContent.
    private static void _BuildTheRailPanel(IDockPanelRegistry panels) =>
        panels.Panels.Single(panel => panel.Id == AssistantIndicatorCoordinator.DockPanelId).CreateView();

    private sealed class _CollectingLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new _CollectingLogger<object>(lines);

        public void Dispose()
        {
        }
    }

    // Only the formatted message is kept: these tests are about a line existing and being one, not its structure.
    private sealed class _CollectingLogger<T>(List<string> lines) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
