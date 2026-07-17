using System.Reflection;
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
/// Hosts every cockpit MCP endpoint (#AC-13, #AC-12): one lightweight loopback MCP server per endpoint, each
/// auto-published into the shared MCP registry as its own entry so it appears in the New-session picker and is
/// tickable per session. Endpoints come from two places — the <see cref="CockpitMcpEndpoint"/>s registered up front
/// (mounted at startup), and ones a plugin mounts at runtime through <see cref="MountAsync"/> (it loads after the
/// host has started). Either way it is "a tools class and a name" with no Kestrel or registry wiring of its own.
/// </summary>
/// <remarks>
/// One HTTP listener per endpoint because the MCP ASP.NET SDK hosts a single tool-set per server; the listeners are
/// loopback on OS-assigned ports, invisible to the operator, who sees only the registry entries. The orchestrator
/// keeps its own server for now (delegation-specific state, withheld from sub-agents); new MCPs go through here.
/// </remarks>
internal sealed class CockpitMcpEndpointHost : IHostedService, ICockpitMcpEndpointHost, ISingletonService, IAsyncDisposable
{
    private readonly IReadOnlyList<CockpitMcpEndpoint> _endpoints;
    private readonly IServiceProvider _services;
    private readonly IMcpServerStore _mcpServerStore;
    private readonly McpAuthKey _authKey;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CockpitMcpEndpointHost> _logger;
    private readonly List<WebApplication> _apps = [];
    private readonly HashSet<string> _mountedNames = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _mountGate = new(1, 1);

    public CockpitMcpEndpointHost(
        IEnumerable<CockpitMcpEndpoint> endpoints,
        IServiceProvider services,
        IMcpServerStore mcpServerStore,
        McpAuthKey authKey,
        ILoggerFactory loggerFactory)
    {
        _endpoints = [.. endpoints];
        _services = services;
        _mcpServerStore = mcpServerStore;
        _authKey = authKey;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CockpitMcpEndpointHost>();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var endpoint in _endpoints)
        {
            try
            {
                // Built the tools instance from the application's own services, so a tool can depend on any
                // registered service (the statusline sink, etc.).
                var tools = ActivatorUtilities.CreateInstance(_services, endpoint.ToolsType);
                await MountAsync(endpoint.ServerName, tools, endpoint.EnabledByDefault, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One endpoint failing to bind or publish must not take down the others or the app; it just will
                // not be available this run.
                _logger.LogWarning(ex, "Could not start cockpit MCP endpoint {ServerName}.", endpoint.ServerName);
            }
        }
    }

    public async Task MountAsync(string serverName, object tools, bool enabledByDefault = true, CancellationToken cancellationToken = default)
    {
        await _mountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Idempotent per name: a plugin re-initialised, or two racing to mount, must not bind a second listener
            // for the same MCP server.
            if (!_mountedNames.Add(serverName))
            {
                return;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Services.AddSingleton(_loggerFactory);

            // Hand the SDK the pre-built tools instance, not its type: WithTools(Type) activates a fresh instance
            // from this endpoint's own slim DI, where the tools' dependencies (resolved from the app's services when
            // the instance was built) do not live — so it would fail to resolve them at the first tool call.
            var mcpBuilder = builder.Services.AddMcpServer().WithHttpTransport();
            _WithToolsInstance(mcpBuilder, tools);

            builder.WebHost.UseKestrel();
            // Port 0: the OS picks a free loopback port, so nothing to configure and no collision with a second cockpit.
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();
            // Guard the endpoint before its tools: a request without this run's key never reaches the tool set (AC-40).
            McpAuthMiddleware.Require(app, _authKey);
            app.MapMcp("/mcp");
            _apps.Add(app);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
            var boundUrl = addresses.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException($"The {serverName} MCP endpoint bound no address.");

            var url = $"{boundUrl.TrimEnd('/')}/mcp";
            await _PublishToRegistryAsync(serverName, enabledByDefault, url, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Cockpit MCP endpoint {ServerName} listening at {McpUrl}.", serverName, url);
        }
        finally
        {
            _mountGate.Release();
        }
    }

    /// <summary>
    /// Publishes (or refreshes) an endpoint's registry entry, the same way the orchestrator does: a default-on
    /// endpoint is (re)asserted enabled on every launch so a stale disabled entry never silently turns it off; a
    /// default-off one keeps the operator's last choice. Only the URL is refreshed (the port is OS-assigned). Scope
    /// is All — these are agent tools for any session kind.
    /// </summary>
    private async Task _PublishToRegistryAsync(string serverName, bool enabledByDefault, string url, CancellationToken cancellationToken)
    {
        var servers = (await _mcpServerStore.LoadAsync(cancellationToken).ConfigureAwait(false)).ToList();
        var existing = servers.FindIndex(server => string.Equals(server.Name, serverName, StringComparison.Ordinal));

        var enabled = ShouldBeEnabled(enabledByDefault, existing < 0 ? null : servers[existing]);

        var entry = new McpServerConfig
        {
            Name = serverName,
            Transport = McpTransport.Http,
            Scope = McpServerScope.All,
            Url = url,
            Enabled = enabled,
            // A cockpit-hosted loopback endpoint: the spawn paths hand a session this run's key for it (AC-40).
            CockpitHosted = true,
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
    /// Whether an endpoint publishes enabled: a default-on one is always (re)asserted enabled, so a stale disabled
    /// entry never silently turns it off (the orchestrator's rule); a default-off one keeps the operator's last
    /// choice, defaulting off. Pulled out so it is testable without a host.
    /// </summary>
    internal static bool ShouldBeEnabled(bool enabledByDefault, McpServerConfig? existingEntry) =>
        enabledByDefault || (existingEntry?.Enabled ?? false);

    // The generic WithTools<TToolType>(builder, TToolType target, JsonSerializerOptions?) overload — the one that
    // registers a pre-built instance. Reached by reflection because the tools type is only known at runtime (a
    // plugin's), and the SDK exposes no non-generic "register this instance" overload for a runtime Type.
    private static readonly MethodInfo _WithToolsGeneric = typeof(McpServerBuilderExtensions).GetMethods()
        .Single(method => method.Name == "WithTools"
            && method.IsGenericMethodDefinition
            && method.GetParameters() is { Length: 3 } parameters
            && parameters[1].ParameterType.IsGenericMethodParameter);

    private static void _WithToolsInstance(IMcpServerBuilder mcpBuilder, object tools) =>
        _WithToolsGeneric.MakeGenericMethod(tools.GetType()).Invoke(null, [mcpBuilder, tools, null]);

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var app in _apps)
        {
            await app.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _mountGate.Dispose();
        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
