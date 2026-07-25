using FluentAssertions;
using Cockpit.Core.Mcp;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// Always-mounted endpoints (<c>cockpit-session</c>): hidden from the pickers, yet reaching every session whatever
/// was selected. The point is that the operator cannot lose their status line by unticking something, so the
/// selection filter must never be able to drop one.
/// </summary>
public class AlwaysMountedServerTests
{
    private static readonly McpServerConfig Session =
        new() { Name = "cockpit-session", CockpitHosted = true, AlwaysMounted = true };

    private static readonly McpServerConfig YouTrack = new() { Name = "youtrack", Command = "npx" };

    private static readonly McpServerConfig AutopilotCeo =
        new() { Name = "autopilot-ceo", CockpitHosted = true, Internal = true };

    [Fact]
    public void ApplySessionSelection_NoSelection_MountsItAlongsideTheOrdinaryServers()
    {
        var effective = McpServerRegistryFilter.ApplySessionSelection([Session, YouTrack], enabledServerNames: null);

        effective.Select(server => server.Name).Should().Contain("cockpit-session");
    }

    /// <summary>The case the flag exists for: a selection that names other servers must not silently drop it.</summary>
    [Fact]
    public void ApplySessionSelection_SelectionThatOmitsIt_MountsItAnyway()
    {
        var selection = new HashSet<string>(["youtrack"], StringComparer.OrdinalIgnoreCase);

        var effective = McpServerRegistryFilter.ApplySessionSelection([Session, YouTrack], selection);

        effective.Select(server => server.Name).Should().BeEquivalentTo(["cockpit-session", "youtrack"]);
    }

    [Fact]
    public void ApplySessionSelection_EmptySelection_StillMountsIt()
    {
        var effective = McpServerRegistryFilter.ApplySessionSelection([Session, YouTrack], new HashSet<string>());

        effective.Select(server => server.Name).Should().Equal("cockpit-session");
    }

    /// <summary>Always-mounted is the opposite arrangement to internal, which stays out unless a launch names it.</summary>
    [Fact]
    public void ApplySessionSelection_InternalEndpoint_IsStillLeftOutOfAnUnselectedSession()
    {
        var effective = McpServerRegistryFilter.ApplySessionSelection([Session, AutopilotCeo], enabledServerNames: null);

        effective.Select(server => server.Name).Should().Equal("cockpit-session");
    }
}
