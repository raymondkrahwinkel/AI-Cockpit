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
        var sink = Substitute.For<ISessionStatuslineSink>();
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
}
