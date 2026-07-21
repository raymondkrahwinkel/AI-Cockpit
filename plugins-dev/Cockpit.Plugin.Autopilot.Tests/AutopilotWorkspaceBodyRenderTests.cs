using Avalonia.Controls;
using Avalonia.LogicalTree;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// The AC-150 workspace body renders its two states on a real (headless) visual tree: the "no run yet" empty state,
/// and — once a run starts through the controller it is attached to — the loaded point in its place.
/// </summary>
[Collection("avalonia")]
public class AutopilotWorkspaceBodyRenderTests
{
    [Fact]
    public void Body_ShowsEmptyState_UntilAStartArrives_ThenTheLoadedRun()
    {
        var runs = new AutopilotRunController();
        var body = new AutopilotWorkspaceBody(Substitute.For<IWorkspaceContext>(), new AutopilotSettings(Substitute.For<IPluginStorage>()), runs);
        var window = new Window { Content = body };
        window.Show();

        _Texts(body).Should().Contain("No run yet");

        runs.Start(new AutopilotRun("youtrack", "AC-150", "trigger title", new Dictionary<string, string>()));

        var texts = _Texts(body);
        texts.Should().Contain("AC-150");
        texts.Should().Contain("youtrack");
        texts.Should().Contain("trigger title");
        texts.Should().NotContain("No run yet");
    }

    private static List<string> _Texts(Control root) =>
        [.. root.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text).OfType<string>()];
}
