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
/// Hosts every cockpit MCP endpoint (#AC-13, #AC-12): one lightweight loopback MCP server per endpoint. Endpoints
/// come from two places — the <see cref="CockpitMcpEndpoint"/>s registered up front (mounted at startup), and ones a
/// plugin mounts at runtime through <see cref="MountAsync"/> (it loads after the host has started). Either way it is
/// "a tools class and a name" with no Kestrel wiring of its own.
/// </summary>
/// <remarks>
/// These are the cockpit's own servers, not the operator's, so they are <em>not</em> written into the user-managed
/// registry (AC-40). The host answers them live as an <see cref="ICockpitInternalMcpProvider"/> — the session
/// fan-out merges them in, while the MCP-servers manager (which reads only the store) never lists them. One HTTP
/// listener per endpoint, loopback on an OS-assigned port, guarded by this run's auth key.
/// </remarks>
internal sealed class CockpitMcpEndpointHost
    : IHostedService, ICockpitMcpEndpointHost, ICockpitInternalMcpProvider, ISingletonService, IAsyncDisposable
{
    private readonly IReadOnlyList<CockpitMcpEndpoint> _endpoints;
    private readonly IServiceProvider _services;
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CockpitMcpEndpointHost> _logger;
    private readonly List<WebApplication> _apps = [];
    private readonly List<MountedEndpoint> _mounted = [];
    private readonly Lock _mountedLock = new();
    private readonly SemaphoreSlim _mountGate = new(1, 1);

    public CockpitMcpEndpointHost(
        IEnumerable<CockpitMcpEndpoint> endpoints,
        IServiceProvider services,
        McpAuthKey authKey,
        SessionMcpKeyring keyring,
        ILoggerFactory loggerFactory)
    {
        _endpoints = [.. endpoints];
        _services = services;
        _authKey = authKey;
        _keyring = keyring;
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
                // registered service (the statusline sink, etc.). An endpoint with no gate is always enabled; one that
                // carries an IsEnabled (AC-34's master switch) is hosted but only advertised to a session while it is on.
                var tools = ActivatorUtilities.CreateInstance(_services, endpoint.ToolsType);
                await MountAsync(endpoint.ServerName, tools, isEnabled: endpoint.IsEnabled, isInternal: endpoint.Internal, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One endpoint failing to bind must not take down the others or the app; it just will not be
                // available this run.
                _logger.LogWarning(ex, "Could not start cockpit MCP endpoint {ServerName}.", endpoint.ServerName);
            }
        }
    }

    public async Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, CancellationToken cancellationToken = default)
    {
        await _mountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Idempotent per name: a plugin re-initialised, or two racing to mount, must not bind a second listener
            // for the same MCP server.
            if (_IsMounted(serverName))
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
            McpAuthMiddleware.Require(app, _authKey, _keyring);
            app.MapMcp("/mcp");
            _apps.Add(app);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
            var boundUrl = addresses.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException($"The {serverName} MCP endpoint bound no address.");

            var url = $"{boundUrl.TrimEnd('/')}/mcp";
            lock (_mountedLock)
            {
                _mounted.Add(new MountedEndpoint(serverName, url, isEnabled ?? (static () => true), isInternal));
            }

            _logger.LogInformation("Cockpit MCP endpoint {ServerName} listening at {McpUrl}.", serverName, url);
        }
        finally
        {
            _mountGate.Release();
        }
    }

    /// <summary>
    /// The cockpit-hosted endpoints as the session fan-out sees them (AC-40): each with its live loopback URL, this
    /// run's auth flag, and its current enabled state (a plugin's toggle, or always on). Never touches the store, so
    /// the operator's MCP-servers manager never lists them.
    /// </summary>
    public IReadOnlyList<McpServerConfig> GetServers()
    {
        lock (_mountedLock)
        {
            return
            [
                .. _mounted.Select(endpoint => new McpServerConfig
                {
                    Name = endpoint.Name,
                    Transport = McpTransport.Http,
                    Scope = McpServerScope.All,
                    Url = endpoint.Url,
                    Enabled = endpoint.IsEnabled(),
                    CockpitHosted = true,
                    Internal = endpoint.Internal,
                }),
            ];
        }
    }

    private bool _IsMounted(string serverName)
    {
        lock (_mountedLock)
        {
            return _mounted.Any(endpoint => string.Equals(endpoint.Name, serverName, StringComparison.Ordinal));
        }
    }

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

    private sealed record MountedEndpoint(string Name, string Url, Func<bool> IsEnabled, bool Internal);
}
