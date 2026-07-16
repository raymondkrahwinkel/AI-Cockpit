using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Hosts every registered <see cref="CockpitMcpEndpoint"/> (#AC-13, #AC-12): one lightweight loopback MCP server
/// per endpoint, each auto-published into the shared MCP registry as its own entry so it appears in the New-session
/// picker and is tickable per session. This is the "add a new cockpit MCP" path — a plugin or a first-party
/// feature registers a <see cref="CockpitMcpEndpoint"/> (a tools class + a name) and gets a working, discoverable
/// MCP server with no Kestrel or registry wiring of its own.
/// </summary>
/// <remarks>
/// One HTTP listener per endpoint because the MCP ASP.NET SDK hosts a single tool-set per server; the listeners are
/// loopback on OS-assigned ports, invisible to the operator, who sees only the registry entries. The orchestrator
/// keeps its own server for now (it has delegation-specific state and is withheld from sub-agents); new MCPs go
/// through here.
/// </remarks>
internal sealed class CockpitMcpEndpointHost : IHostedService, ISingletonService, IAsyncDisposable
{
    private readonly IReadOnlyList<CockpitMcpEndpoint> _endpoints;
    private readonly IServiceProvider _services;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CockpitMcpEndpointHost> _logger;
    private readonly List<WebApplication> _apps = [];

    public CockpitMcpEndpointHost(
        IEnumerable<CockpitMcpEndpoint> endpoints,
        IServiceProvider services,
        IMcpServerStore mcpServerStore,
        ILoggerFactory loggerFactory)
    {
        _endpoints = [.. endpoints];
        _services = services;
        _mcpServerStore = mcpServerStore;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CockpitMcpEndpointHost>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in _endpoints)
        {
            try
            {
                await _StartEndpointAsync(endpoint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One endpoint failing to bind or publish must not take down the others or the app; it just will
                // not be available this run.
                _logger.LogWarning(ex, "Could not start cockpit MCP endpoint {ServerName}.", endpoint.ServerName);
            }
        }
    }

    private async Task _StartEndpointAsync(CockpitMcpEndpoint endpoint, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);

        // Build the tools instance from the application's own services, so a tool can depend on any registered
        // service (the statusline sink, etc.), then hand that instance to the endpoint's MCP server.
        var tools = ActivatorUtilities.CreateInstance(_services, endpoint.ToolsType);
        builder.Services.AddSingleton(endpoint.ToolsType, tools);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools([endpoint.ToolsType]);

        builder.WebHost.UseKestrel();
        // Port 0: the OS picks a free loopback port, so nothing to configure and no collision with a second cockpit.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();
        app.MapMcp("/mcp");
        _apps.Add(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"The {endpoint.ServerName} MCP endpoint bound no address.");

        var url = $"{boundUrl.TrimEnd('/')}/mcp";
        await _PublishToRegistryAsync(endpoint, url, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Cockpit MCP endpoint {ServerName} listening at {McpUrl}.", endpoint.ServerName, url);
    }

    /// <summary>
    /// Publishes (or refreshes) the endpoint's registry entry, the same way the orchestrator does: an
    /// <see cref="CockpitMcpEndpoint.EnabledByDefault"/> endpoint is (re)asserted enabled on every launch so a
    /// stale disabled entry never silently turns it off; a non-default one keeps the operator's last choice. Only
    /// the URL is refreshed (the port is OS-assigned). Scope is All — these are agent tools for any session kind.
    /// </summary>
    private async Task _PublishToRegistryAsync(CockpitMcpEndpoint endpoint, string url, CancellationToken cancellationToken)
    {
        var servers = (await _mcpServerStore.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var existing = servers.FindIndex(server => string.Equals(server.Name, endpoint.ServerName, StringComparison.Ordinal));

        var enabled = ShouldBeEnabled(endpoint, existing < 0 ? null : servers[existing]);

        var entry = new McpServerConfig
        {
            Name = endpoint.ServerName,
            Transport = McpTransport.Http,
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
    }

    /// <summary>
    /// Whether an endpoint publishes enabled: an <see cref="CockpitMcpEndpoint.EnabledByDefault"/> one is always
    /// (re)asserted enabled, so a stale disabled entry never silently turns it off (the orchestrator's rule); a
    /// non-default one keeps the operator's last choice, defaulting off. Pulled out so it is testable without a host.
    /// </summary>
    internal static bool ShouldBeEnabled(CockpitMcpEndpoint endpoint, McpServerConfig? existingEntry) =>
        endpoint.EnabledByDefault || (existingEntry?.Enabled ?? false);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var app in _apps)
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
