using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>
/// <see cref="AutopilotMcpTools"/> — the in-process report endpoint (AC-153). A gate report is trusted only when the
/// transport-verified caller pane is the run's own embedded session; a report from any other pane is refused, so an
/// agent cannot settle a run that is not its own.
/// </summary>
public class AutopilotMcpToolsTests
{
    private static AutopilotRunController _RunningController(string paneId)
    {
        var controller = new AutopilotRunController(new AutopilotSettings(Substitute.For<IPluginStorage>()));
        controller.BeginScoping(new AutopilotRun("youtrack", "AC-153", "done-gate", new Dictionary<string, string>()));
        controller.MarkRunning();
        controller.BindSession(paneId);
        return controller;
    }

    [Fact]
    public void ReportGate_FromAnotherPane_IsRefused_AndRecordsNothing()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("some-other-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);

        tools.ReportGate("security", "passed", null).Should().Contain("\"ok\":false");
        runs.Gates.Should().BeEmpty();
    }

    [Fact]
    public void ReportGate_FromTheRunsOwnPane_RecordsTheOutcome()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("run-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);

        tools.ReportGate("security", "passed", "reviewed, no findings").Should().Contain("\"ok\":true");
        runs.Gates.Should().ContainKey(GateKind.Security).WhoseValue.Should().Be(AutopilotGateOutcome.Passed);
    }

    [Fact]
    public void ReportGate_WithAnUnknownGate_IsRefused()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("run-pane");
        var tools = new AutopilotMcpTools(host, _RunningController("run-pane"));

        tools.ReportGate("nonsense", "passed", null).Should().Contain("\"ok\":false");
    }

    [Fact]
    public void MarkReady_FromTheRunsOwnPane_SettlesTheRun()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("run-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);
        tools.ReportGate("security", "passed", null);

        tools.MarkReady();

        runs.Phase.Should().Be(AutopilotRunPhase.MergeReady);
    }

    [Fact]
    public void Blocked_FromTheRunsOwnPane_MovesTheRunToAwaitingOperator()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("run-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);

        tools.Blocked("Which database?").Should().Contain("\"ok\":true");
        runs.Phase.Should().Be(AutopilotRunPhase.AwaitingOperator);
        runs.PendingQuestion.Should().Be("Which database?");
    }

    [Fact]
    public void Blocked_FromAnotherPane_IsRefused_AndDoesNotBlock()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("intruder-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);

        tools.Blocked("Which database?").Should().Contain("\"ok\":false");
        runs.Phase.Should().Be(AutopilotRunPhase.Running);
    }

    [Fact]
    public void Blocked_WhenTheRunIsNotRunning_IsRefused()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("run-pane");
        var runs = _RunningController("run-pane");
        runs.MarkReady();

        var tools = new AutopilotMcpTools(host, runs);

        runs.Phase.Should().NotBe(AutopilotRunPhase.Running);
        tools.Blocked("late question").Should().Contain("\"ok\":false");
    }

    [Fact]
    public void MarkReady_FromAnotherPane_IsRefused_AndDoesNotSettle()
    {
        var host = Substitute.For<ICockpitHost>();
        host.CurrentMcpCallerPaneId.Returns("intruder-pane");
        var runs = _RunningController("run-pane");
        var tools = new AutopilotMcpTools(host, runs);

        tools.MarkReady().Should().Contain("\"ok\":false");
        runs.Phase.Should().Be(AutopilotRunPhase.Running);
    }
}
