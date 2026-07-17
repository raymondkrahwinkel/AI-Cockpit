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
using Cockpit.Infrastructure.Mcp;

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
internal sealed class OrchestratorMcpServer
    : IHostedService, IOrchestratorServerState, ICockpitInternalMcpProvider, IDelegationMcpToggle, ISingletonService, IAsyncDisposable
{
    /// <summary>The MCP server name, shared with the spawn paths that decide whether a session gets these tools.</summary>
    public const string ServerName = DelegationMcp.ServerName;

    private readonly IDelegationService _delegation;
    private readonly McpAuthKey _authKey;
    private readonly IDelegationSettingsStore _settingsStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<OrchestratorMcpServer> _logger;
    private WebApplication? _app;
    private volatile bool _mcpEnabled = true;

    public OrchestratorMcpServer(
        IDelegationService delegation,
        McpAuthKey authKey,
        IDelegationSettingsStore settingsStore,
        ILoggerFactory loggerFactory)
    {
        _delegation = delegation;
        _authKey = authKey;
        _settingsStore = settingsStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<OrchestratorMcpServer>();
    }

    public string? OrchestratorMcpUrl { get; private set; }

    /// <summary>Whether the orchestrator MCP is offered to sessions (AC-40) — the Options toggle, loaded on start.</summary>
    public bool McpEnabled => _mcpEnabled;

    public async Task SetMcpEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        _mcpEnabled = enabled;
        await _settingsStore.SaveAsync(new DelegationSettings { McpEnabled = enabled }, cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _mcpEnabled = (await _settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false)).McpEnabled;

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
        // Guard the endpoint before its tools: a request without this run's key never reaches delegation (AC-40).
        McpAuthMiddleware.Require(_app, _authKey);
        _app.MapMcp("/mcp");

        await _app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The orchestrator MCP server bound no address.");

        OrchestratorMcpUrl = $"{boundUrl.TrimEnd('/')}/mcp";
        _logger.LogInformation("Orchestrator MCP server listening at {McpUrl}", OrchestratorMcpUrl);
    }

    /// <summary>
    /// The orchestrator as the session fan-out sees it (AC-40): its live loopback URL, this run's auth flag, and the
    /// operator's on/off from the Options toggle — answered live rather than published into the registry, so the
    /// MCP-servers manager never lists it. Empty until the server has bound its port.
    /// </summary>
    public IReadOnlyList<McpServerConfig> GetServers() =>
        OrchestratorMcpUrl is { } url
            ?
            [
                new McpServerConfig
                {
                    Name = ServerName,
                    Transport = McpTransport.Http,
                    // All sessions, not Claude-only: a local model orchestrating cheap sub-tasks is exactly the
                    // kind of thing this is for, and the tool loop speaks the same HTTP MCP.
                    Scope = McpServerScope.All,
                    Url = url,
                    Enabled = _mcpEnabled,
                    CockpitHosted = true,
                },
            ]
            : [];

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
