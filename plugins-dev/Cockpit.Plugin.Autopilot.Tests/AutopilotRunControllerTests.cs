using FluentAssertions;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotRunController"/> — the shared handoff between the "start" intent and the workspace body (AC-150).
/// </summary>
public class AutopilotRunControllerTests
{
    private static AutopilotRun Run(string issue) =>
        new("youtrack", issue, issue, new Dictionary<string, string>());

    [Fact]
    public void Start_SetsCurrent_AndRaisesChanged()
    {
        var controller = new AutopilotRunController();
        var fired = 0;
        controller.CurrentChanged += (_, _) => fired++;

        controller.Current.Should().BeNull();
        controller.Start(Run("AC-150"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-150" });
        fired.Should().Be(1);
    }

    [Fact]
    public void Start_Twice_ReplacesCurrent_WithTheLatestRun()
    {
        var controller = new AutopilotRunController();

        controller.Start(Run("AC-1"));
        controller.Start(Run("AC-2"));

        controller.Current.Should().BeEquivalentTo(new { IssueId = "AC-2" });
    }
}
