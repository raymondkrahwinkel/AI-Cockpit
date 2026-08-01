using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Assistant;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Infrastructure.Tests.Assistant;

/// <summary>
/// The first gate of AC-544's mount rule, read off the <em>real</em> registration rather than a fixture.
/// </summary>
/// <remarks>
/// <b>Why this file exists separately from <c>AssistantReadMountRuleTests</c>.</b> Those tests hand-build an
/// <c>McpServerConfig</c> with <c>Internal = true</c> to prove the selection filter treats such a server correctly.
/// That is a true statement about the filter and no statement at all about this endpoint: drop <c>Internal: true</c>
/// from <c>DependencyInjection</c> — copy the <c>cockpit-agents</c> line above it, say, which is
/// <c>AlwaysMounted: true</c> — and every one of those tests still passes while the broad read tools quietly fan out
/// to every session in the cockpit. The fixture cannot catch that, because the fixture is not what ships.
/// <para>
/// So this asserts on the container: what the app actually registers, resolved the way the endpoint host resolves
/// it. It is the difference between testing the rule and testing that the rule was applied.
/// </para>
/// </remarks>
public sealed class AssistantEndpointRegistrationTests
{
    /// <summary>
    /// The registered endpoints, resolved as <c>CockpitMcpEndpointHost</c> resolves them. Only the endpoint records
    /// themselves are constructed — they are registered as instances, so nothing else in the container is built.
    /// </summary>
    private static IReadOnlyList<CockpitMcpEndpoint> _Endpoints() =>
        [.. new ServiceCollection().AddInfrastructure().BuildServiceProvider().GetServices<CockpitMcpEndpoint>()];

    [Fact]
    public void TheAssistantsReadEndpoint_IsRegistered()
    {
        // Without this the feature does not exist at all: the tools are written, and nothing hosts them.
        Assert.Contains(_Endpoints(), endpoint => endpoint.ServerName == AssistantIdentity.McpServerName);
    }

    [Fact]
    public void TheAssistantsReadEndpoint_IsInternal_SoItNeverFansOutToASessionThatDidNotNameIt()
    {
        var endpoint = Assert.Single(_Endpoints(), endpoint => endpoint.ServerName == AssistantIdentity.McpServerName);

        Assert.True(endpoint.Internal, "The assistant's read endpoint must be Internal, or it fans out to every session.");
    }

    [Fact]
    public void TheAssistantsReadEndpoint_IsNotAlwaysMounted()
    {
        // The neighbouring registration (cockpit-agents) is AlwaysMounted, which is the opposite arrangement and the
        // likeliest thing for this line to be turned into by a copy-paste. AlwaysMounted wins over Internal in
        // McpServerRegistryFilter, so setting it here would hand the cross-workspace read path to every session
        // while still reading, at a glance, like a deliberate line about the assistant.
        var endpoint = Assert.Single(_Endpoints(), endpoint => endpoint.ServerName == AssistantIdentity.McpServerName);

        Assert.False(endpoint.AlwaysMounted);
    }

    [Fact]
    public void TheAssistantsReadEndpoint_HasNoLiveGate_SoItIsNotSilentlyOffWhenTheAssistantAsks()
    {
        // An IsEnabled gate here would make the tools vanish for reasons unrelated to the mount rule, and the
        // assistant would report "I cannot see any sessions" rather than "that server is off" — the shape of silent
        // wrongness criterion 6 exists to prevent.
        var endpoint = Assert.Single(_Endpoints(), endpoint => endpoint.ServerName == AssistantIdentity.McpServerName);

        Assert.Null(endpoint.IsEnabled);
    }
}
