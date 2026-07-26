using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The cockpit-session <c>set_status</c> tool routing to the label sink (#AC-13, #AC-312). The Kestrel hosting itself
/// needs a real server, so it is out of unit-test reach here.
/// </summary>
public class CockpitMcpEndpointTests
{
    [Fact]
    public async Task SetStatus_RoutesToTheSink_AndReportsWhetherASessionMatched()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync("pane-1", "AC-13").Returns(Task.FromResult(true));
        sink.SetStatuslineAsync("unknown", Arg.Any<string>()).Returns(Task.FromResult(false));
        var tools = new SessionStatusTools(sink);

        var ok = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("pane-1", "AC-13"));
        ok.GetProperty("ok").GetBoolean().Should().BeTrue();
        ok.GetProperty("status").GetString().Should().Be("AC-13");

        // An id that matches no session is reported honestly, so the agent can fix the id rather than assume it worked.
        var missed = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("unknown", "AC-13"));
        missed.GetProperty("ok").GetBoolean().Should().BeFalse();
        missed.TryGetProperty("error", out _).Should().BeTrue();
    }

    // A status without a name must not touch the title: an agent that says what it is doing has not asked to be
    // renamed, and a session silently relabelled on every status update would be the opposite of the AC-310 rule.
    [Fact]
    public async Task SetStatus_WithoutAName_LeavesTheTitleAlone()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        var tools = new SessionStatusTools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("pane-1", "AC-13"));

        await sink.DidNotReceive().SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>());
        reply.TryGetProperty("renamed", out _).Should().BeFalse("a reply that never asked about the name should not answer about it");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetStatus_WithAName_ProposesIt_AndSaysWhetherItWasTaken(bool taken)
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(true));
        sink.SuggestNameAsync("pane-1", "AC-312").Returns(Task.FromResult(taken));
        var tools = new SessionStatusTools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("pane-1", "AC-13", "AC-312"));

        // False is not a failure: the session keeps a name somebody chose, and the agent is told so rather than left
        // believing it renamed anything.
        reply.GetProperty("ok").GetBoolean().Should().BeTrue();
        reply.GetProperty("renamed").GetBoolean().Should().Be(taken);
    }

    // Nothing to rename on a session that does not exist — and the reply is the same "fix your id" error as before,
    // not a half-success that says the name was considered.
    [Fact]
    public async Task SetStatus_OnASessionThatDoesNotExist_DoesNotProposeAName()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(Task.FromResult(false));
        var tools = new SessionStatusTools(sink);

        var reply = JsonSerializer.Deserialize<JsonElement>(await tools.SetStatusAsync("unknown", "AC-13", "AC-312"));

        await sink.DidNotReceive().SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>());
        reply.GetProperty("ok").GetBoolean().Should().BeFalse();
    }
}
