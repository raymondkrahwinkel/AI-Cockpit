using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;
using Xunit.Abstractions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// Renders the Autopilot workspace body (AC-94) to a real, themed PNG — the operator's view of a live run: the header,
/// the pipeline status strip with its tracker/issue chips and reported done-gates, and the isolated agent session
/// embedded below. This is the repeatable replacement for the throwaway app-Screenshotter scene: it keeps the shipped
/// app free of any reference to the plugin's internals, since only this test project reaches across.
/// <para>
/// It doubles as a render smoke test (a captured frame proves the surface builds and paints under the real theme). To
/// save the image somewhere durable, point <c>AUTOPILOT_SCREENSHOT_OUT</c> at a path; otherwise it lands next to the
/// test binary. Regenerate with:
/// <c>AUTOPILOT_SCREENSHOT_OUT=/path/autopilot-workspace.png dotnet test --filter FullyQualifiedName~AutopilotScreenshot</c>
/// </para>
/// </summary>
[Collection("avalonia")]
public class AutopilotScreenshotTests
{
    private readonly ITestOutputHelper _output;

    public AutopilotScreenshotTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Renders_TheRunningWorkspace_WithReportedGates_ToAThemedPng()
    {
        var host = Substitute.For<ICockpitHost>();
        host.RequestConsentAsync(Arg.Any<ConsentRequest>()).Returns(new ConsentDecision(ConsentOutcome.Approved));

        var embedded = Substitute.For<IEmbeddedSession>();
        embedded.View.Returns(_SessionPlaceholder());
        embedded.PaneId.Returns("pane-demo");
        embedded.CloseAsync().Returns(Task.CompletedTask);

        var context = Substitute.For<IWorkspaceContext>();
        context.EmbedSession(Arg.Any<EmbeddedSessionRequest>()).Returns(embedded);

        var settings = new AutopilotSettings(Substitute.For<IPluginStorage>());
        var runs = new AutopilotRunController(settings);
        var body = new AutopilotWorkspaceBody(host, context, settings, runs);

        var window = new Window { Width = 1100, Height = 760, Content = body };
        try
        {
            window.Show();

            runs.BeginScoping(new AutopilotRun("youtrack", "AC-72", "Add a copy button to the transcript view", new Dictionary<string, string>()));
            runs.MarkRunning();
            Dispatcher.UIThread.RunJobs();

            runs.ReportGate(GateKind.Verify, AutopilotGateOutcome.Passed);
            runs.ReportGate(GateKind.CodeReview, AutopilotGateOutcome.Passed);
            runs.ReportGate(GateKind.Security, AutopilotGateOutcome.Passed);
            Dispatcher.UIThread.RunJobs();

            var frame = window.CaptureRenderedFrame();
            frame.Should().NotBeNull("the headless renderer must paint the themed surface");

            var outputPath = _ResolveOutputPath();
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            frame!.Save(outputPath);
            new FileInfo(outputPath).Length.Should().BeGreaterThan(1000, "a real rendered PNG is not near-empty");
            _output.WriteLine($"Autopilot workspace screenshot written to {Path.GetFullPath(outputPath)}");
        }
        finally
        {
            window.Close();
        }
    }

    private static string _ResolveOutputPath() =>
        Environment.GetEnvironmentVariable("AUTOPILOT_SCREENSHOT_OUT") is { Length: > 0 } path
            ? path
            : Path.Combine(AppContext.BaseDirectory, "autopilot-workspace.png");

    // The embedded pane is a real session at runtime; for the still it stands in as a muted panel so the surface reads
    // as "the run's session lives here" without pulling a live agent into a unit test.
    private static Control _SessionPlaceholder() => new Border
    {
        Background = _Brush("CockpitSecondaryBgBrush"),
        Child = new TextBlock
        {
            Text = "the run's live agent session appears here",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.6,
        },
    };

    private static IBrush? _Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush ? brush : null;
}
