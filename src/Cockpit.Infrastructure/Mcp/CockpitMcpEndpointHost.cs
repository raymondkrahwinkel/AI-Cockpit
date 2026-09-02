using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Mcp;

// AC-13/AC-12: hosts one loopback MCP server per cockpit endpoint, mounted at startup or by a plugin via
// MountAsync; not written into the user-managed registry (AC-40). AC-790/AC-791: with the node switch on, each
// endpoint but Internal also gets an HTTPS listener guarded by a persistent shared secret.
internal sealed class CockpitMcpEndpointHost
    : IHostedService, ICockpitMcpEndpointHost, ICockpitInternalMcpProvider, ISingletonService, IAsyncDisposable
{
    private readonly IReadOnlyList<CockpitMcpEndpoint> _endpoints;
    private readonly IServiceProvider _services;
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly INodeEndpointSettingsStore _nodeEndpointSettings;
    private readonly NodeSelfSignedCertificate _nodeCertificate;
    private readonly NodeSharedSecret _nodeSharedSecret;
    private readonly SessionMcpMounts _mounts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CockpitMcpEndpointHost> _logger;
    private readonly List<WebApplication> _apps = [];
    private readonly List<MountedEndpoint> _mounted = [];
    private readonly Lock _mountedLock = new();
    private readonly SemaphoreSlim _mountGate = new(1, 1);

    // Loaded once, not once per endpoint: the class-level comment already promises the switch only takes effect on
    // the next launch, so it cannot change mid-run — re-reading and re-decrypting the whole of cockpit.json on
    // every one of the ~7 startup mounts would be pure waste for a value this run will never see change.
    private NodeEndpointSettings? _nodeSettings;

    // Same reasoning as _nodeSettings: the machine's LAN-facing address does not change between one mount and the
    // next within a single run, so a full network-interface enumeration per endpoint is redundant. A separate
    // "resolved" flag because the resolved value itself is legitimately null (no usable interface found).
    private string? _nodeReachableAddress;
    private bool _nodeReachableAddressResolved;

    // Whether the live shared-secret holder has been seeded from disk — see `MountAsync` for why this is a flag
    // and not just a comment. Guarded by `_mountGate`, like the mount it belongs to.
    private bool _nodeSecretSeeded;

    public CockpitMcpEndpointHost(
        IEnumerable<CockpitMcpEndpoint> endpoints,
        IServiceProvider services,
        McpAuthKey authKey,
        SessionMcpKeyring keyring,
        INodeEndpointSettingsStore nodeEndpointSettings,
        NodeSelfSignedCertificate nodeCertificate,
        NodeSharedSecret nodeSharedSecret,
        SessionMcpMounts mounts,
        ILoggerFactory loggerFactory)
    {
        _endpoints = [.. endpoints];
        _services = services;
        _authKey = authKey;
        _keyring = keyring;
        _nodeEndpointSettings = nodeEndpointSettings;
        _nodeCertificate = nodeCertificate;
        _nodeSharedSecret = nodeSharedSecret;
        _mounts = mounts;
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
                await _MountAsync(endpoint.ServerName, tools, endpoint.IsEnabled, endpoint.Internal, endpoint.AlwaysMounted, endpoint.NodeOnly, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One endpoint failing to bind must not take down the others or the app; it just will not be
                // available this run.
                _logger.LogWarning(ex, "Could not start cockpit MCP endpoint {ServerName}.", endpoint.ServerName);
            }
        }
    }

    // A plugin's endpoint is never NodeOnly: that flag exists for this cockpit's own node-control endpoint, and a
    // plugin has no way to ask for a listener the operator's switches do not govern.
    public Task MountAsync(string serverName, object tools, Func<bool>? isEnabled = null, bool isInternal = false, bool alwaysMounted = false, CancellationToken cancellationToken = default) =>
        _MountAsync(serverName, tools, isEnabled, isInternal, alwaysMounted, nodeOnly: false, cancellationToken);

    private async Task _MountAsync(string serverName, object tools, Func<bool>? isEnabled, bool isInternal, bool alwaysMounted, bool nodeOnly, CancellationToken cancellationToken)
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

            // AC-527: every endpoint this host mounts carries the agent line's mail on its tool results, resolved
            // from the application's services rather than this endpoint's slim container.
            mcpBuilder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                CallToolResult result;
                try
                {
                    var expectedParameters = _ExpectedParameterNames(context.MatchedPrimitive);
                    var unknownParameters = context.MatchedPrimitive is McpServerTool tool && context.Params.Arguments is { } arguments
                        ? UnknownParameterNames(tool.ProtocolTool.InputSchema, arguments.Keys)
                        : null;
                    result = unknownParameters is { Count: > 0 }
                        ? _UnknownToolArgumentsResult(context.Params.Name, unknownParameters, expectedParameters)
                        : await next(context, cancellationToken).ConfigureAwait(false);
                }
                // ArgumentException narrowed to the marshaller's own signature (ParamName "arguments") so a bug
                // inside a tool's own logic is never mislabelled as a bad call; JsonException is left broad, since
                // only argument deserialization throws it here.
                catch (Exception exception) when (exception is ArgumentException { ParamName: "arguments" } or JsonException)
                {
                    // AC-1028: surfaced as a readable result naming the bad parameter and the tool's full parameter
                    // list, so the calling agent can self-correct instead of reading this as "the tool is broken".
                    _logger.LogWarning(exception, "Tool {Tool} was called with an invalid argument.", context.Params.Name);
                    result = _ToolArgumentErrorResult(context, exception);
                }
                // AC-1138: a tool that gave up waiting for the UI thread answers with its own code rather than a
                // generic failure — the caller can tell "the cockpit is busy, try again" from "this went wrong",
                // and nothing it asked for was applied.
                catch (UiUnavailableException exception)
                {
                    _logger.LogWarning("Tool {Tool} gave up waiting for the UI thread after {Deadline}.", context.Params.Name, exception.Deadline);
                    result = _UiUnavailableResult(exception);
                }
                catch (UiOutcomeUnknownException exception)
                {
                    _logger.LogWarning("Tool {Tool} timed out after UI work had begun at {Deadline}.", context.Params.Name, exception.Deadline);
                    result = _UiOutcomeUnknownResult(exception);
                }

                return McpInboxPiggyback.Attach(result, _services.GetService<IAgentTurnInboxDelivery>(), _logger);
            }));

            var nodeSettings = _nodeSettings ??= await _nodeEndpointSettings.LoadAsync(cancellationToken).ConfigureAwait(false);

            // AC-791: an Internal endpoint (AC-204) gets no network listener whatever the master switch says —
            // withholding the socket rather than refusing leaves a remote prober nothing to learn. AC-1148: nor
            // does one the operator switched off, so the switch decides the bind and not only the advertisement.
            var enabled = isEnabled ?? (static () => true);
            var bindNodeListener = nodeSettings.Enabled && !isInternal && enabled();

            builder.WebHost.ConfigureKestrel(options =>
            {
                // Port 0 lets the OS pick a free loopback port. IPv4 specifically, not ListenLocalhost — that binds
                // both families on one shared dynamic port, which Kestrel refuses.
                options.Listen(System.Net.IPAddress.Loopback, 0);
                if (bindNodeListener)
                {
                    // A second, network-reachable listener next to the loopback one (AC-790), guarded by the persistent
                    // shared secret rather than this run's ephemeral McpAuthKey — see McpAuthMiddleware.Require below.
                    options.Listen(System.Net.IPAddress.Any, 0, listenOptions => listenOptions.UseHttps(_nodeCertificate.Value));
                }
            });

            if (bindNodeListener && !_nodeSecretSeeded)
            {
                // AC-792: seed the live holder from disk only once — MountAsync may run long after startup, and
                // repeating this would overwrite _nodeSettings with a secret since rotated or cleared.
                _nodeSharedSecret.Set(nodeSettings.SharedSecret);
                _nodeSecretSeeded = true;
            }

            var app = builder.Build();
            // Guard the endpoint before its tools: a request without this run's key never reaches the tool set (AC-40),
            // and AC-1148: nor does one this endpoint's own mount decision never granted.
            McpAuthMiddleware.Require(
                app,
                _authKey,
                _keyring,
                paneId => _AuthorizeAsync(paneId, serverName, enabled),
                bindNodeListener ? _nodeSharedSecret : null);
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
                _mounted.Add(new MountedEndpoint(serverName, url, enabled, isInternal, alwaysMounted, nodeUrl, nodeOnly));
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
                    // AC-1148: a NodeOnly endpoint is never offered to a session — it exists for the paired
                    // controller, which does not read this list.
                    Enabled = endpoint.IsEnabled() && !endpoint.NodeOnly,
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

    // AC-1148: the mount decision this host already makes, enforced at the door. Every input is read now rather
    // than captured at mount time, so a master switch flipped off or a pairing narrowed applies to the next call.
    private async ValueTask<bool> _AuthorizeAsync(string? paneId, string serverName, Func<bool> isEnabled)
    {
        var nodeScopeGranted = paneId == NodeCallerIdentity.PaneId && await _NodeScopeGrantedAsync().ConfigureAwait(false);
        return McpEndpointAuthorization.Allows(paneId, serverName, isEnabled(), nodeScopeGranted, _mounts);
    }

    // AC-794: a pairing grants nothing until the operator ticks a profile or a project, so an empty scope reaches
    // no endpoint. Resolved from the service provider rather than injected — the broker asks this host for its
    // node addresses, and taking it as a constructor dependency closes that cycle.
    private async ValueTask<bool> _NodeScopeGrantedAsync()
    {
        if (_services.GetService<INodePairingBroker>() is not { } broker)
        {
            return false;
        }

        await broker.EnsureLoadedAsync().ConfigureAwait(false);
        return broker.Pairing is { } pairing
            && (pairing.AllowedProfileLabels.Count > 0 || pairing.AllowedProjectIds.Count > 0);
    }

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
    private static void _WithToolsInstance(IMcpServerBuilder mcpBuilder, object tools) =>
        ResolveWithToolsGeneric(typeof(McpServerBuilderExtensions)).MakeGenericMethod(tools.GetType()).Invoke(null, [mcpBuilder, tools, null]);

    internal static MethodInfo ResolveWithToolsGeneric(Type extensionsType)
    {
        var methods = extensionsType.GetMethods();
        var matches = methods.Where(method => method.Name == "WithTools"
                && method.IsGenericMethodDefinition
                && method.GetParameters() is { Length: 3 } parameters
                && parameters[1].ParameterType.IsGenericMethodParameter)
            .ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        var overloads = methods.Where(method => method.Name == "WithTools")
            .Select(method => $"{method.Name}({string.Join(", ", method.GetParameters().Select(parameter => parameter.ParameterType.Name))})")
            .DefaultIfEmpty("none");
        throw new InvalidOperationException($"Could not resolve the generic WithTools(builder, target, options) overload in {extensionsType.FullName}. Found WithTools overloads: {string.Join("; ", overloads)}.");
    }

    // Turns a marshalling failure (a missing required argument, or one that will not deserialize) into a tool
    // result the calling agent can act on (AC-1028). The parameter list comes from the tool's own advertised
    // schema, not a hand-maintained list, so it can never drift from what the tool actually accepts.
    private static CallToolResult _ToolArgumentErrorResult(RequestContext<CallToolRequestParams> context, Exception exception)
    {
        var parameters = _ExpectedParameterNames(context.MatchedPrimitive);
        var message = parameters.Count > 0
            ? $"{context.Params.Name}: {exception.Message} Expected parameters: {string.Join(", ", parameters)}."
            : $"{context.Params.Name}: {exception.Message}";

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = message }] };
    }

    private static CallToolResult _UnknownToolArgumentsResult(string toolName, IReadOnlyList<string> parameters, IReadOnlyList<string> expectedParameters) =>
        new()
        {
            IsError = true,
            Content = [new TextContentBlock
            {
                Text = $"{toolName}: {string.Join(" ", parameters.Select(parameter => _UnknownParameterMessage(parameter, expectedParameters)))}",
            }],
        };

    private static string _UnknownParameterMessage(string parameter, IReadOnlyList<string> expectedParameters)
    {
        var suggestion = expectedParameters.FirstOrDefault(candidate => parameter.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
        return suggestion is null
            ? $"Unknown parameter '{parameter}'."
            : $"Unknown parameter '{parameter}'; did you mean '{suggestion}'?";
    }

    // AC-1138: the same `ok`/`error` shape the tools themselves answer in, plus the code an agent branches on.
    private static CallToolResult _UiUnavailableResult(UiUnavailableException exception)
    {
        var payload = JsonSerializer.Serialize(
            new { ok = false, code = UiUnavailableException.Code, error = exception.Message });

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = payload }] };
    }

    private static CallToolResult _UiOutcomeUnknownResult(UiOutcomeUnknownException exception)
    {
        var payload = JsonSerializer.Serialize(
            new { ok = false, code = UiOutcomeUnknownException.Code, error = exception.Message });

        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = payload }] };
    }

    private static IReadOnlyList<string> _ExpectedParameterNames(IMcpServerPrimitive? primitive) =>
        primitive is McpServerTool tool
            ? _SchemaParameterNames(tool.ProtocolTool.InputSchema) ?? []
            : [];

    // Suggestion matching is deliberately prefix-only: it fixes profileLabel → profile without pretending to be fuzzy search.
    internal static IReadOnlyList<string>? UnknownParameterNames(JsonElement inputSchema, IEnumerable<string> parameters)
    {
        var expectedParameters = _SchemaParameterNames(inputSchema);
        if (expectedParameters is null)
        {
            return null;
        }

        return [.. parameters.Where(parameter => !expectedParameters.Contains(parameter, StringComparer.Ordinal))];
    }

    private static IReadOnlyList<string>? _SchemaParameterNames(JsonElement inputSchema) =>
        inputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            ? [.. properties.EnumerateObject().Select(property => property.Name)]
            : null;

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

        // The certificate is not disposed here: it is a shared singleton this machine's identity lives in, and
        // the pairing host holds the same instance.
        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record MountedEndpoint(string Name, string Url, Func<bool> IsEnabled, bool Internal, bool AlwaysMounted = false, string? NodeUrl = null, bool NodeOnly = false);
}
