using System.Text.Json;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The cockpit-session <c>set_status</c> tool routing to the label sink (#AC-13, #AC-312). The Kestrel hosting itself
/// needs a real server, so it is out of unit-test reach here.
/// </summary>
public class CockpitMcpEndpointTests
{
    // AC-1094 added start_run/run_status to the same tools class; this file only exercises set_status, so the
    // extra dependencies are unused substitutes here rather than real collaborators.
    private static SessionStatusTools _Tools(ISessionLabelSink sink) => new(
        sink,
        Substitute.For<ITrackedCommandRunner>(),
        new RunTracker(),
        Substitute.For<IWorkspaceAgentGateway>(),
        Substitute.For<IWorkspaceAgentCoordinator>(),
        Substitute.For<IAgentMessageInbox>());

    [Fact]
    public async Task SetStatus_RoutesToTheSink_AndReportsWhetherASessionMatched()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync("pane-1", "AC-13").Returns(Task.FromResult(true));
        sink.SetStatuslineAsync("unknown", Arg.Any<string>()).Returns(Task.FromResult(false));
        var tools = _Tools(sink);

        var ok = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("AC-13", "pane-1"));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        Assert.Equal("AC-13", ok.GetProperty("status").GetString());

        // An id that matches no session is reported honestly, so the agent can fix the id rather than assume it worked.
        var missed = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("AC-13", "unknown"));
        Assert.False(missed.GetProperty("ok").GetBoolean());
        Assert.True(missed.TryGetProperty("error", out _));
    }

    // A status without a name must not touch the title: an agent that says what it is doing has not asked to be
    // renamed, and a session silently relabelled on every status update would be the opposite of the AC-310 rule.
    [Fact]
    public async Task SetStatus_WithoutAName_LeavesTheTitleAlone()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        var tools = _Tools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("AC-13", "pane-1"));

        await sink.DidNotReceive().SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(reply.TryGetProperty("renamed", out _), "a reply that never asked about the name should not answer about it");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetStatus_WithAName_ProposesIt_AndSaysWhetherItWasTaken(bool taken)
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        sink.SuggestNameAsync("pane-1", "AC-312").Returns(Task.FromResult(taken));
        var tools = _Tools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("AC-13", "pane-1", "AC-312"));

        // False is not a failure: the session keeps a name somebody chose, and the agent is told so rather than left
        // believing it renamed anything.
        Assert.True(reply.GetProperty("ok").GetBoolean());
        Assert.Equal(taken, reply.GetProperty("renamed").GetBoolean());
    }

    // Nothing to rename on a session that does not exist — and the reply is the same "fix your id" error as before,
    // not a half-success that says the name was considered.
    [Fact]
    public async Task SetStatus_OnASessionThatDoesNotExist_DoesNotProposeAName()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));
        var tools = _Tools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("AC-13", "unknown", "AC-312"));

        await sink.DidNotReceive().SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>());
        Assert.False(reply.GetProperty("ok").GetBoolean());
    }
}
