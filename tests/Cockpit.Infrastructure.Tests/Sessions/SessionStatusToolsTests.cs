using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.Infrastructure.Tests.Sessions;

/// <summary>
/// AC-128: set_status keys on the transport-verified pane, not the agent-declared <c>session</c>, so an agent cannot
/// spoof or clear another session's statusline by naming its id (confused deputy) — the AC-89 pattern the terminal
/// tools already hold.
/// </summary>
public class SessionStatusToolsTests
{
    [Fact]
    public async Task SetStatus_KeysOnTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tools = new SessionStatusTools(sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            // The agent spoofs another session's id in the tool argument.
            await tools.SetStatusAsync("pwned", "victim-pane");

            // The status lands on the verified caller, never the spoofed id.
            await sink.Received(1).SetStatuslineAsync("verified-pane", "pwned");
            await sink.DidNotReceive().SetStatuslineAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // The name travels on the same tool call, so it inherits the same hazard: without this it would be a second way
    // to reach a session you do not own, and a renamed session is more disruptive than a rewritten status line.
    [Fact]
    public async Task SetStatus_ProposesTheNameToTheVerifiedPane_NotTheAgentSuppliedSessionId()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        sink.SuggestNameAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tools = new SessionStatusTools(sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            await tools.SetStatusAsync("pwned", "victim-pane", "pwned-name");

            await sink.Received(1).SuggestNameAsync("verified-pane", "pwned-name");
            await sink.DidNotReceive().SuggestNameAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // AC-1028: `session` is only a fallback for the unverified in-process path — on the transport-verified path it
    // is not needed at all, so omitting it must succeed rather than throw a marshalling error.
    [Fact]
    public async Task SetStatus_SucceedsWithoutSession_OnTheVerifiedPath()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        sink.SetStatuslineAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        var tools = new SessionStatusTools(sink);

        McpRequestContext.Set("verified-pane");
        try
        {
            await tools.SetStatusAsync("AC-1028");

            await sink.Received(1).SetStatuslineAsync("verified-pane", "AC-1028");
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }

    // Off the verified path (the in-process tool loop / tests) there is no middleware to trust, so a caller that
    // gives no `session` either gets a readable error, not an exception or a silently ignored call.
    [Fact]
    public async Task SetStatus_ReturnsAReadableError_WhenUnverifiedAndNoSessionGiven()
    {
        var sink = Substitute.For<ISessionLabelSink>();
        var tools = new SessionStatusTools(sink);

        var result = await tools.SetStatusAsync("AC-1028");

        Assert.Contains("session", result, StringComparison.OrdinalIgnoreCase);
        await sink.DidNotReceiveWithAnyArgs().SetStatuslineAsync(default!, default!);
    }
}
