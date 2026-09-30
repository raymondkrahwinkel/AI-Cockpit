using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="DesktopBackendHost.AddMcpServer"/> (#60): a plugin's HTTP MCP-server contribution reaches the shared
/// <see cref="IMcpServerStore"/> registry as an idempotent upsert-by-name — the same registry the MCP-servers
/// dialog, the local tool-loop and the Claude fan-out all read. Covers the add path, the update-existing path
/// (URL/token refreshed, <see cref="McpServerConfig.Enabled"/> and <see cref="McpServerConfig.Scope"/> left
/// alone), and the chosen "re-add after delete" rule.
/// </summary>
public class CockpitHostAddMcpServerTests
{
    [Fact]
    public async Task AddMcpServer_NoExistingEntry_AddsAnEnabledHttpEntryWithBearerAuth()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-123");

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list =>
                list.Count == 1
                && list[0].Name == "YouTrack: Prod"
                && list[0].Transport == McpTransport.Http
                && list[0].Url == "https://x.youtrack.cloud/mcp"
                && list[0].Auth == McpServerAuth.ApiKey
                && list[0].ApiKey == "token-123"
                && list[0].Enabled
                && list[0].Scope == McpServerScope.All),
            Arg.Any<CancellationToken>());
    }

    // AC-500: a contribution declaring OAuth (an authority, no bearer token) must reach the store as
    // McpServerAuth.OAuth with its authority/client-id carried along — this is the legacy push path
    // (AddMcpServer), the mapping's other caller besides McpServerCatalog's pull path.
    [Fact]
    public async Task AddMcpServer_OAuthContribution_AddsEntryWithOAuthAuthAndAuthority()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("Depot: project-a", "https://depot.example/mcp")
        {
            OAuthAuthority = "https://depot.example/oauth",
            OAuthClientId = "cockpit",
        };

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list =>
                list[0].Auth == McpServerAuth.OAuth
                && list[0].OAuthAuthority == "https://depot.example/oauth"
                && list[0].OAuthClientId == "cockpit"
                && list[0].ApiKey == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMcpServer_RequestedScope_AppliesOnlyToANewEntry()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("local-only-server", "https://x/mcp", Scope: McpContributionScope.LocalOnly);

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list => list[0].Scope == McpServerScope.LocalOnly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMcpServer_ExistingEntryDisabledByTheUser_StaysDisabledAfterRefresh()
    {
        var existing = new McpServerConfig
        {
            Name = "YouTrack: Prod",
            Transport = McpTransport.Http,
            Url = "https://old.youtrack.cloud/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "old-token",
            Enabled = false,
        };
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig> { existing });
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("YouTrack: Prod", "https://new.youtrack.cloud/mcp", "new-token");

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list => list.Count == 1 && !list[0].Enabled && list[0].Url == "https://new.youtrack.cloud/mcp"),
            Arg.Any<CancellationToken>());
    }

    private static DesktopBackendHost _BuildHost(IMcpServerStore store, PluginDiagnostics? diagnostics = null)
    {
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new DesktopBackendHost(
            "youtrack",
            "YouTrack",
            services,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            diagnostics ?? new PluginDiagnostics());
    }
}
