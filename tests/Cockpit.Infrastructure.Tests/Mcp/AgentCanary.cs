using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Infrastructure.Agents;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.Tests.Mcp;

/// <summary>
/// The canary (AC-616): a desk of cockpit MCP endpoints and a bare MCP client per pane, with no model anywhere.
/// <para>
/// It exists because of what an agent cannot test about the line it is on. An agent cannot see how its message
/// renders for the recipient — the sending side reports nothing about that — and it cannot put arbitrary bytes in a
/// tool call, so a sanitiser reporting "nothing stripped" proves nothing. Both need a recipient that reports exactly
/// what arrived, which is what this is.
/// </para>
/// <para>
/// Deliberately the whole stack: Kestrel on a loopback port, the auth middleware that stamps the verified pane, the
/// call-tool filter AC-527 added, and the real tools. The seam that ticket lives on is the one between those, so a
/// test that stopped short of the transport would be testing everything except the part that is new.
/// </para>
/// </summary>
internal sealed class AgentCanaryDesk : IAsyncDisposable
{
    private readonly CockpitMcpEndpointHost _host;
    private readonly IReadOnlyList<AgentCanaryPane> _panes;
    private readonly string _auditPath;

    /// <summary>The roster the mounted endpoint is using, for a test that wants to arrange or assert against it directly.</summary>
    public WorkspaceAgentCoordinator Roster { get; }

    /// <summary>The inbox the mounted endpoint is using.</summary>
    public AgentMessageInbox Mailbox { get; }

    private AgentCanaryDesk(
        CockpitMcpEndpointHost host,
        IReadOnlyList<AgentCanaryPane> panes,
        WorkspaceAgentCoordinator roster,
        AgentMessageInbox mailbox,
        string auditPath)
    {
        _host = host;
        _panes = panes;
        _auditPath = auditPath;
        Roster = roster;
        Mailbox = mailbox;
    }

    /// <summary>
    /// Starts a desk with the named panes on it and the <c>cockpit-agents</c> endpoint mounted the way the app mounts
    /// it. The workspace gateway is a stub over the pane list: what is under test here is the delivery seam, not the
    /// App-layer rule for which panes share a desk — that has its own tests, against real session panels.
    /// </summary>
    public static async Task<AgentCanaryDesk> StartAsync(params string[] paneIds)
    {
        var roster = new WorkspaceAgentCoordinator();
        var mailbox = new AgentMessageInbox();
        var auditPath = Path.Combine(Path.GetTempPath(), $"canary-audit-{Guid.NewGuid():N}.jsonl");

        var services = new ServiceCollection();
        services.AddSingleton<IWorkspaceAgentGateway>(new CanaryWorkspaceGateway(paneIds));
        services.AddSingleton<IWorkspaceAgentCoordinator>(roster);
        services.AddSingleton<IAgentMessageInbox>(mailbox);
        services.AddSingleton<IAgentResourceClaims>(new AgentResourceClaims());
        services.AddSingleton<IAgentTurnInboxDelivery>(new AgentTurnInboxDelivery(mailbox, roster));
        // Far out of the way: the rate limit has its own tests, and a canary loop is exactly the traffic shape it
        // would otherwise stop.
        services.AddSingleton<IAgentLineBudget>(new AgentLineBudget(TimeProvider.System, TimeSpan.FromMinutes(1), 10_000, 10_000));
        services.AddSingleton<IAgentNotifyAuditLog>(new AgentNotifyAuditLog(auditPath, NullLogger<AgentNotifyAuditLog>.Instance));

        var keyring = new SessionMcpKeyring();
        var host = new CockpitMcpEndpointHost(
            [new CockpitMcpEndpoint("cockpit-agents", typeof(AgentsMcpTools))],
            services.BuildServiceProvider(),
            new McpAuthKey(),
            keyring,
            NullLoggerFactory.Instance);

        await host.StartAsync(CancellationToken.None);
        var url = host.GetServers().Single(server => server.Name == "cockpit-agents").Url!;

        // Each pane gets its own bearer, so the server attributes its calls exactly as it would a real session's —
        // the identity under test is the transport's, and handing every client the same token would test nothing.
        var panes = paneIds.Select(paneId => new AgentCanaryPane(paneId, url, keyring.TokenFor(paneId))).ToArray();

        return new AgentCanaryDesk(host, panes, roster, mailbox, auditPath);
    }

    public AgentCanaryPane Pane(string paneId) =>
        _panes.Single(pane => string.Equals(pane.PaneId, paneId, StringComparison.Ordinal));

    public async ValueTask DisposeAsync()
    {
        foreach (var pane in _panes)
        {
            await pane.DisposeAsync();
        }

        await _host.StopAsync(CancellationToken.None);
        await _host.DisposeAsync();

        if (File.Exists(_auditPath))
        {
            File.Delete(_auditPath);
        }
    }

    /// <summary>
    /// A desk of exactly the panes it was told about, all resolving to one snapshot — which is what one desk means.
    /// <c>DeliversAtTurnStart</c> is false for all of them on purpose: a canary pane has no turn the host could add
    /// to, which is precisely the TTY-shaped case AC-527 exists for.
    /// </summary>
    private sealed class CanaryWorkspaceGateway(string[] paneIds) : IWorkspaceAgentGateway
    {
        private readonly WorkspaceAgentSnapshot _snapshot = new(
            "canary-desk",
            [.. paneIds.Select(paneId => new WorkspaceAgentPane(paneId, paneId, null, string.Empty, DeliversAtTurnStart: false))]);

        public Task<WorkspaceAgentSnapshot?> GetWorkspaceSnapshotAsync(string paneId) =>
            Task.FromResult<WorkspaceAgentSnapshot?>(paneIds.Contains(paneId, StringComparer.Ordinal) ? _snapshot : null);

        // There is no turn to start on a canary pane, and saying so is honest where a stub claiming Woken would not
        // be. Every wake-side assertion belongs to WorkspaceAgentGatewayWakeTests, against real session panels.
        public Task<AgentWakeOutcome> TryWakeAsync(string callerPaneId, string targetPaneId, string kind) =>
            Task.FromResult(AgentWakeOutcome.CannotTakeATurn);
    }
}

/// <summary>
/// One pane's MCP client: a bare connection carrying that pane's own bearer, reporting what came back verbatim.
/// </summary>
internal sealed class AgentCanaryPane(string paneId, string url, string token) : IAsyncDisposable
{
    private McpClient? _client;

    public string PaneId { get; } = paneId;

    private async Task<McpClient> _ClientAsync() =>
        _client ??= await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "cockpit-agents",
            Endpoint = new Uri(url),
            TransportMode = HttpTransportMode.AutoDetect,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        }));

    /// <summary>
    /// Calls a tool and hands back every text block as it arrived, in order and unparsed. Unparsed on purpose: this
    /// instrument exists to assert on characters, and anything that read the JSON and returned a shape would throw
    /// away the thing being measured.
    /// </summary>
    public async Task<IReadOnlyList<string>> CallAsync(string tool, Dictionary<string, object?>? arguments = null)
    {
        var client = await _ClientAsync();
        var result = await client.CallToolAsync(tool, arguments ?? []);
        return [.. result.Content.OfType<TextContentBlock>().Select(block => block.Text)];
    }

    /// <summary>The whole response as one string, for a test about what is present rather than about which block holds it.</summary>
    public async Task<string> CallForTextAsync(string tool, Dictionary<string, object?>? arguments = null) =>
        string.Join("\n", await CallAsync(tool, arguments));

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
