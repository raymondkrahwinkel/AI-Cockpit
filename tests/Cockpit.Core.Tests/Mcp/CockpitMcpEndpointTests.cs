using System.Text.Json;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The cockpit-session <c>set_status</c> tool routing to the statusline sink (#AC-13). The Kestrel hosting itself
/// needs a real server, so it is out of unit-test reach here.
/// </summary>
public class CockpitMcpEndpointTests
{
    [Fact]
    public async Task SetStatus_RoutesToTheSink_AndReportsWhetherASessionMatched()
    {
        var sink = Substitute.For<ISessionStatuslineSink>();
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
}
