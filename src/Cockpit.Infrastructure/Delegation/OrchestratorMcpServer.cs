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
    /// <summary>The MCP server name; the tools appear to a session as <c>mcp__cockpit-orchestrator__delegate_task</c> and friends.</summary>
    public const string ServerName = "cockpit-orchestrator";

    private readonly IDelegationService _delegation;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorMcpServer> _logger;
    private WebApplication? _app;

    public OrchestratorMcpServer(IDelegationService delegation, IMcpServerStore mcpServerStore, ILoggerFactory loggerFactory)
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
    /// Registered <b>disabled</b>: handing a session the ability to spawn work under other profiles is a decision
    /// the operator makes, never a default. The URL is rewritten on every start, since the port is OS-assigned.
    /// </remarks>
    private async Task _PublishToRegistryAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var servers = (await _mcpServerStore.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existing = servers.FindIndex(server => string.Equals(server.Name, ServerName, StringComparison.Ordinal));

            if (existing < 0)
            {
                servers.Add(new McpServerConfig
                {
                    Name = ServerName,
                    Transport = McpTransport.Http,
                    // All sessions, not Claude-only: a local model orchestrating cheap sub-tasks is exactly the
                    // kind of thing this is for, and the tool loop speaks the same HTTP MCP.
                    Scope = McpServerScope.All,
                    Url = url,
                    Enabled = false,
                });
            }
            else
            {
                // Keep whatever the operator chose about enabling it; only the address moves.
                servers[existing] = servers[existing] with { Url = url, Transport = McpTransport.Http, Scope = McpServerScope.All };
            }

            await _mcpServerStore.SaveAsync(servers, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The cockpit still runs without delegation; a failure here must not stop the app from starting.
            _logger.LogWarning(ex, "Could not publish the orchestrator MCP server into the registry.");
        }
    }

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
