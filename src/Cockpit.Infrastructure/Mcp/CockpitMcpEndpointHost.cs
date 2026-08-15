using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// Hosts every cockpit MCP endpoint (#AC-13, #AC-12): one lightweight loopback MCP server per endpoint. Endpoints
// come from two places — the `CockpitMcpEndpoint`s registered up front (mounted at startup), and ones a
// plugin mounts at runtime through `MountAsync` (it loads after the host has started). Either way it is
// "a tools class and a name" with no Kestrel wiring of its own.
// These are the cockpit's own servers, not the operator's, so they are *not* written into the user-managed
// registry (AC-40). The host answers them live as an `ICockpitInternalMcpProvider` — the session
// fan-out merges them in, while the MCP-servers manager (which reads only the store) never lists them. One HTTP
// listener per endpoint, loopback on an OS-assigned port, guarded by this run's auth key.
// AC-790: when the network-node master switch is on, each endpoint also gets a second, HTTPS listener on a
// network interface, guarded by a persistent shared secret instead of this run's ephemeral key — off by default,
// and read once at mount time, so flipping the setting takes effect on the next launch, not live.
// AC-791: each endpoint except an `Internal` one, which stays loopback-only however the switch is set — see
// `MountAsync`, and `NodeCallerIdentity` for what a caller that does reach a node listener is allowed to be.
internal sealed class CockpitMcpEndpointHost
    : IHostedService, ICockpitMcpEndpointHost, ICockpitInternalMcpProvider, ISingletonService, IAsyncDisposable
{
    private readonly IReadOnlyList<CockpitMcpEndpoint> _endpoints;
    private readonly IServiceProvider _services;
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly INodeEndpointSettingsStore _nodeEndpointSettings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CockpitMcpEndpointHost> _logger;
    private readonly List<WebApplication> _apps = [];
    private readonly List<MountedEndpoint> _mounted = [];
    private readonly Lock _mountedLock = new();
    private readonly SemaphoreSlim _mountGate = new(1, 1);

    // Lazy: a cockpit that never turns node binding on for this run never pays for a certificate it does not need.
    private X509Certificate2? _nodeCertificate;

    // Loaded once, not once per endpoint: the class-level comment already promises the switch only takes effect on
    // the next launch, so it cannot change mid-run — re-reading and re-decrypting the whole of cockpit.json on
    // every one of the ~7 startup mounts would be pure waste for a value this run will never see change.
    private NodeEndpointSettings? _nodeSettings;

    // Same reasoning as _nodeSettings: the machine's LAN-facing address does not change between one mount and the
    // next within a single run, so a full network-interface enumeration per endpoint is redundant. A separate
    // "resolved" flag because the resolved value itself is legitimately null (no usable interface found).
    private string? _nodeReachableAddress;
    private bool _nodeReachableAddressResolved;

    public CockpitMcpEndpointHost(
        IEnumerable<CockpitMcpEndpoint> endpoints,
        IServiceProvider services,
        McpAuthKey authKey,
        SessionMcpKeyring keyring,
        INodeEndpointSettingsStore nodeEndpointSettings,
        ILoggerFactory loggerFactory)
    {
        _endpoints = [.. endpoints];
        _services = services;
        _authKey = authKey;
        _keyring = keyring;
        _nodeEndpointSettings = nodeEndpointSettings;
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
                await MountAsync(endpoint.ServerName, tools, isEnabled: endpoint.IsEnabled, isInternal: endpoint.Internal, alwaysMounted: endpoint.AlwaysMounted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One endpoint failing to bind must not take down the others or the app; it just will not be
                // available this run.
                _logger.LogWarning(ex, "Could not start cockpit MCP endpoint {ServerName}.", endpoint.ServerName);
            }
        }
    }

    public async Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, bool alwaysMounted = false, CancellationToken cancellationToken = default)
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

            // AC-527: one registration, and every endpoint this host mounts carries the agent line's mail out on its
            // tool results — the ones registered up front and the ones a plugin mounts later, without any of them
            // knowing the inbox exists. Deliberately not narrowed to the cockpit-agents server: the value of this
            // route is that *any* tool call an agent makes is a chance to reach it, and a pane that spends its day in
            // cockpit-session or a plugin's tools is exactly the pane the old routes could not reach.
            //
            // The delivery service is resolved from the application's services, not this endpoint's slim container —
            // the same reason WithTools takes a pre-built instance here rather than a type.
            mcpBuilder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
                McpInboxPiggyback.Attach(
                    await next(context, cancellationToken).ConfigureAwait(false),
                    _services.GetService<IAgentTurnInboxDelivery>(),
                    _logger)));

            var nodeSettings = _nodeSettings ??= await _nodeEndpointSettings.LoadAsync(cancellationToken).ConfigureAwait(false);

            // AC-791: an internal endpoint (AC-204 — the assistant's read and act tools) gets no network listener,
            // whatever the master switch says. Internal already means "reaches only a launch that names it by
            // name", and every one of those launches is a session on this machine; a caller from another machine
            // cannot be one of them by construction, so there is nothing for it to reach here. Withholding the
            // listener rather than refusing the request is deliberate: an endpoint that binds no socket off this
            // machine cannot be opened later by a scoping mistake in whatever authorizes remote callers, and it
            // gives a prober nothing to learn — the port is not there, so there is no answer to read.
            var bindNodeListener = nodeSettings.Enabled && !isInternal;

            builder.WebHost.ConfigureKestrel(options =>
            {
                // Port 0: the OS picks a free loopback port, so nothing to configure and no collision with a second
                // cockpit. IPv4 loopback specifically, not ListenLocalhost — that binds both 127.0.0.1 and [::1] on
                // one shared dynamic port, which Kestrel refuses ("dynamic port binding is not supported when
                // binding to localhost") since the OS could hand the two families different ports.
                options.Listen(System.Net.IPAddress.Loopback, 0);
                if (bindNodeListener)
                {
                    // A second, network-reachable listener next to the loopback one (AC-790), guarded by the persistent
                    // shared secret rather than this run's ephemeral McpAuthKey — see McpAuthMiddleware.Require below.
                    options.Listen(System.Net.IPAddress.Any, 0, listenOptions => listenOptions.UseHttps(_GetOrCreateNodeCertificate()));
                }
            });

            var app = builder.Build();
            // Guard the endpoint before its tools: a request without this run's key never reaches the tool set (AC-40).
            McpAuthMiddleware.Require(app, _authKey, _keyring, bindNodeListener ? nodeSettings.SharedSecret : null);
            app.MapMcp("/mcp");
            _apps.Add(app);

            await app.StartAsync(cancellationToken).ConfigureAwait(false);

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
                ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");

            // The loopback listener's own address, kept as the endpoint's `Url` — unchanged external behaviour for
            // every caller that only ever knew this one listener.
            var loopbackUrl = addresses.Addresses.FirstOrDefault(address => address.Contains("127.0.0.1", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The {serverName} MCP endpoint bound no loopback address.");
            var url = $"{loopbackUrl.TrimEnd('/')}/mcp";

            // The node listener's address, translated from Kestrel's wildcard bind into something reachable from
            // another machine — "https://0.0.0.0:PORT" means nothing to a second cockpit's operator.
            string? nodeUrl = null;
            if (bindNodeListener)
            {
                var nodeListenAddress = addresses.Addresses.FirstOrDefault(address => address.StartsWith("https://", StringComparison.Ordinal));
                if (nodeListenAddress is not null && _GetReachableAddress() is { } reachableHost)
                {
                    var nodePort = new Uri(nodeListenAddress).Port;
                    nodeUrl = $"https://{reachableHost}:{nodePort}/mcp";
                }
            }

            lock (_mountedLock)
            {
                _mounted.Add(new MountedEndpoint(serverName, url, isEnabled ?? (static () => true), isInternal, alwaysMounted, nodeUrl));
            }

            _logger.LogInformation("Cockpit MCP endpoint {ServerName} listening at {McpUrl}.", serverName, url);
        }
        finally
        {
            _mountGate.Release();
        }
    }

    // The cockpit-hosted endpoints as the session fan-out sees them (AC-40): each with its live loopback URL, this
    // run's auth flag, and its current enabled state (a plugin's toggle, or always on). Never touches the store, so
    // the operator's MCP-servers manager never lists them.
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
                    AlwaysMounted = endpoint.AlwaysMounted,
                }),
            ];
        }
    }

    // This instance's live network-node addresses (AC-790) — see ICockpitInternalMcpProvider.GetNodeAddresses.
    public IReadOnlyList<NodeEndpointAddress> GetNodeAddresses()
    {
        lock (_mountedLock)
        {
            return
            [
                .. _mounted
                    .Where(endpoint => endpoint.NodeUrl is not null)
                    .Select(endpoint => new NodeEndpointAddress(endpoint.Name, endpoint.NodeUrl!)),
            ];
        }
    }

    private X509Certificate2 _GetOrCreateNodeCertificate() => _nodeCertificate ??= NodeSelfSignedCertificate.Create();

    private string? _GetReachableAddress()
    {
        if (!_nodeReachableAddressResolved)
        {
            _nodeReachableAddress = NodeReachableAddress.Resolve();
            _nodeReachableAddressResolved = true;
        }

        return _nodeReachableAddress;
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
        _nodeCertificate?.Dispose();
        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record MountedEndpoint(string Name, string Url, Func<bool> IsEnabled, bool Internal, bool AlwaysMounted = false, string? NodeUrl = null);
}
