using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Delegation;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Delegation;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Delegation;

/// <summary>
/// Hosts the <c>cockpit-orchestrator</c> MCP server (#67): one Kestrel endpoint on loopback exposing the
/// delegation tools, running for the app's lifetime. Same shape as the permission server, which is the
/// in-process MCP hosting this app already proves works against the real CLI.
/// </summary>
/// <remarks>
/// The server is hosted whether or not anything uses it; a session only gets these tools if it was started with
/// delegation enabled, which is what keeps a delegated sub-agent from being handed the very tools it would need
/// to delegate on.
/// </remarks>
internal sealed class OrchestratorMcpServer : IHostedService, IOrchestratorServerState, ISingletonService, IAsyncDisposable
{
    /// <summary>The MCP server name, shared with the spawn paths that decide whether a session gets these tools.</summary>
    public const string ServerName = DelegationMcp.ServerName;

    private readonly IDelegationService _delegation;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorMcpServer> _logger;
    private WebApplication? _app;

    public OrchestratorMcpServer(
        IDelegationService delegation,
        IMcpServerStore mcpServerStore,
        ILoggerFactory loggerFactory)
    {
        _delegation = delegation;
        _mcpServerStore = mcpServerStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OrchestratorMcpServer>();
    }

    public string? OrchestratorMcpUrl { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);
        builder.Services.AddSingleton(_delegation);
        builder.Services.AddSingleton<OrchestratorTools>();
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<OrchestratorTools>();

        builder.WebHost.UseKestrel();

        // Port 0: the OS picks a free one. A fixed port would collide with a second cockpit — and with whatever
        // else happens to hold it.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        _app = builder.Build();
        _app.MapMcp("/mcp");

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The orchestrator MCP server bound no address.");

        OrchestratorMcpUrl = $"{boundUrl.TrimEnd('/')}/mcp";
        await _PublishToRegistryAsync(OrchestratorMcpUrl, cancellationToken);
        _logger.LogInformation("Orchestrator MCP server listening at {McpUrl}", OrchestratorMcpUrl);
    }

    /// <summary>
    /// Publishes the server into the shared MCP registry, the same way a plugin publishes one. That makes
    /// delegation an ordinary MCP server you can see in Options and tick per session — which is exactly the
    /// opt-in the design asks for on the calling side, without a second, parallel mechanism.
    /// </summary>
    /// <remarks>
    /// Enabled by default (on first registration): the tools are worth having from the first run — and
    /// <c>add_profile</c> in particular has to work <em>before</em> any delegation target exists, or the first one
    /// could never be scaffolded through it. On later starts the operator's own on/off choice is kept, and only
    /// the URL is refreshed (the port is OS-assigned). It used to be rewritten to "enabled only if a target
    /// exists" on every start, which reset both the default and any manual toggle — the reason it read as off
    /// every time. Whether a session actually receives these tools is still gated per session, so "enabled" here
    /// is availability, not a blanket hand-out.
    /// </remarks>
    private async Task _PublishToRegistryAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var servers = (await _mcpServerStore.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existing = servers.FindIndex(server => string.Equals(server.Name, ServerName, StringComparison.Ordinal));

            // First registration: on. Thereafter: whatever the operator last chose — the app refreshes the URL but
            // does not touch their switch.
            var enabled = ShouldBeEnabled(existing < 0 ? null : servers[existing]);

            var entry = new McpServerConfig
            {
                Name = ServerName,
                Transport = McpTransport.Http,
                // All sessions, not Claude-only: a local model orchestrating cheap sub-tasks is exactly the
                // kind of thing this is for, and the tool loop speaks the same HTTP MCP.
                Scope = McpServerScope.All,
                Url = url,
                Enabled = enabled,
            };

            if (existing < 0)
            {
                servers.Add(entry);
            }
            else
            {
                servers[existing] = entry;
            }

            await _mcpServerStore.SaveAsync(servers, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Orchestrator MCP server published ({State}) at {Url}.", enabled ? "enabled" : "disabled", url);
        }
        catch (Exception ex)
        {
            // The cockpit still runs without delegation; a failure here must not stop the app from starting.
            _logger.LogWarning(ex, "Could not publish the orchestrator MCP server into the registry.");
        }
    }

    /// <summary>
    /// Whether the orchestrator server is enabled on this publish: on when it has never been registered, otherwise
    /// whatever the operator last set. Pulled out so the "on by default, then leave the operator's switch alone"
    /// rule is testable without standing up the Kestrel host.
    /// </summary>
    internal static bool ShouldBeEnabled(McpServerConfig? existingEntry) => existingEntry is null || existingEntry.Enabled;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            await _app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
