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
            await tools.SetStatusAsync("victim-pane", "pwned");

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
            await tools.SetStatusAsync("victim-pane", "pwned", "pwned-name");

            await sink.Received(1).SuggestNameAsync("verified-pane", "pwned-name");
            await sink.DidNotReceive().SuggestNameAsync("victim-pane", Arg.Any<string>());
        }
        finally
        {
            McpRequestContext.Set(null);
        }
    }
}
