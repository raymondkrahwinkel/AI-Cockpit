using System.Text.Json;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Cockpit.Infrastructure.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// The generic cockpit MCP endpoint mechanism (#AC-13, #AC-12): an endpoint's on-by-default publish rule, and the
/// cockpit-session <c>set_status</c> tool routing to the statusline sink. The Kestrel hosting itself needs a real
/// server, so it is out of unit-test reach here — these cover the two decisions that carry the behaviour.
/// </summary>
public class CockpitMcpEndpointTests
{
    [Fact]
    public void ShouldBeEnabled_ADefaultOnEndpoint_IsAlwaysReasserted_EvenIfPreviouslyDisabled()
    {
        var endpoint = new CockpitMcpEndpoint("cockpit-session", typeof(SessionStatusTools), EnabledByDefault: true);

        CockpitMcpEndpointHost.ShouldBeEnabled(endpoint, existingEntry: null).Should().BeTrue();
        CockpitMcpEndpointHost.ShouldBeEnabled(endpoint, new McpServerConfig { Name = "cockpit-session", Enabled = false }).Should().BeTrue();
    }

    [Fact]
    public void ShouldBeEnabled_ADefaultOffEndpoint_KeepsTheOperatorsChoice_DefaultingOff()
    {
        var endpoint = new CockpitMcpEndpoint("cockpit-extra", typeof(SessionStatusTools), EnabledByDefault: false);

        CockpitMcpEndpointHost.ShouldBeEnabled(endpoint, existingEntry: null).Should().BeFalse();
        CockpitMcpEndpointHost.ShouldBeEnabled(endpoint, new McpServerConfig { Name = "cockpit-extra", Enabled = true }).Should().BeTrue();
    }

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
