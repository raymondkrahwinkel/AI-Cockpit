using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Sessions;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddMcpServer"/> (#60): a plugin's HTTP MCP-server contribution reaches the shared
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

    [Fact]
    public async Task AddMcpServer_NoBearerToken_AddsEntryWithNoAuth()
    {
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("open-server", "https://open.example.com/mcp");

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list => list[0].Auth == McpServerAuth.None && list[0].ApiKey == null),
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
    public async Task AddMcpServer_ExistingEntry_RefreshesUrlAndTokenOnly_WithoutDuplicating()
    {
        var existing = new McpServerConfig
        {
            Name = "YouTrack: Prod",
            Transport = McpTransport.Http,
            Url = "https://old.youtrack.cloud/mcp",
            Auth = McpServerAuth.ApiKey,
            ApiKey = "old-token",
            Scope = McpServerScope.ClaudeOnly,
            Enabled = true,
        };
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig> { existing });
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("YouTrack: Prod", "https://new.youtrack.cloud/mcp", "new-token");

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list =>
                list.Count == 1
                && list[0].Url == "https://new.youtrack.cloud/mcp"
                && list[0].ApiKey == "new-token"
                // The pre-existing scope is preserved even though the contribution's default is McpContributionScope.All.
                && list[0].Scope == McpServerScope.ClaudeOnly),
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

    [Fact]
    public async Task AddMcpServer_EntryPreviouslyDeletedByTheUser_IsAddedBackAsANewEnabledEntry()
    {
        // Documents the chosen trade-off (#60): the store has no concept of "deleted on purpose" versus
        // "never registered", so a removed entry looks identical to an absent one and is re-added as fresh
        // (enabled) on the plugin's next explicit trigger (Initialize / settings-saved) — not a background
        // loop fighting the user, but not permanently respected either.
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(new List<McpServerConfig>());
        var host = _BuildHost(store);
        var contribution = new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-123");

        await host.AddMcpServer(contribution);

        await store.Received(1).SaveAsync(
            Arg.Is<IReadOnlyList<McpServerConfig>>(list => list.Count == 1 && list[0].Enabled),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMcpServer_CalledTwiceWithTheSameName_NeverProducesADuplicate()
    {
        var servers = new List<McpServerConfig>();
        var store = Substitute.For<IMcpServerStore>();
        store.LoadAsync(Arg.Any<CancellationToken>()).Returns(_ => servers);
        store.SaveAsync(Arg.Any<IReadOnlyList<McpServerConfig>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(call => servers = [.. call.Arg<IReadOnlyList<McpServerConfig>>()]);
        var host = _BuildHost(store);

        await host.AddMcpServer(new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-1"));
        await host.AddMcpServer(new McpServerContribution("YouTrack: Prod", "https://x.youtrack.cloud/mcp", "token-2"));

        servers.Should().ContainSingle();
        servers[0].ApiKey.Should().Be("token-2");
    }

    private static CockpitHost _BuildHost(IMcpServerStore store)
    {
        var services = new ServiceCollection().AddSingleton(store).BuildServiceProvider();
        return new CockpitHost(
            "youtrack",
            "YouTrack",
            services,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance);
    }
}
