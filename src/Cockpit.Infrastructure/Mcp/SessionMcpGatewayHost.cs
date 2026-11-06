using System.Collections.Concurrent;
using System.Net;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Abstractions.Projects;
using Cockpit.Core.Abstractions.Agents;
using Cockpit.Core.Abstractions.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed partial class SessionMcpGatewayHost : IHostedService, IAsyncDisposable, ISessionMcpGatewayState
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CockpitConfigFileAccess _configFile;
    private readonly Func<McpToolProvider?>? _provider;
    private readonly SessionMcpMounts? _mounts;
    private readonly IAgentTurnInboxDelivery? _inbox;
    private readonly IProjectStore? _projects;
    private WebApplication? _app;

    public SessionMcpGatewayHost(McpAuthKey authKey, SessionMcpKeyring keyring, ILoggerFactory loggerFactory, Func<IMcpToolProvider>? provider = null, SessionMcpMounts? mounts = null, IAgentTurnInboxDelivery? inbox = null, IProjectStore? projects = null)
        : this(authKey, keyring, loggerFactory, CockpitConfigPath.Default, mounts: mounts, inbox: inbox)
    {
        _provider = () => provider?.Invoke() as McpToolProvider;
        _projects = projects;
    }

    internal SessionMcpGatewayHost(McpAuthKey authKey, SessionMcpKeyring keyring, ILoggerFactory loggerFactory, string configPath, McpToolProvider? provider = null, SessionMcpMounts? mounts = null, IAgentTurnInboxDelivery? inbox = null)
    {
        _authKey = authKey;
        _keyring = keyring;
        _loggerFactory = loggerFactory;
        _configFile = new CockpitConfigFileAccess(configPath);
        _provider = () => provider;
        _mounts = mounts;
        _inbox = inbox;
        _cleanup = new(loggerFactory.CreateLogger<SessionMcpGatewayHost>());
    }
    internal string? BoundUrl { get; private set; }
    public bool IsEnabled => BoundUrl is not null;

    public string? UrlFor(string paneId) => BoundUrl is { } url
        ? $"{url}/s/{Uri.EscapeDataString(paneId)}/mcp"
        : null;

    internal async Task BindLaunchAsync(string paneId, string? projectId, string? workingDirectory, string? confineRoot, string? agentName, bool assistantManaged, CancellationToken ct, IReadOnlyDictionary<string, string?>? environment = null)
    {
        var project = _projects is null ? null : (await _projects.LoadAsync(ct).ConfigureAwait(false)).Find(projectId);
        _mounts?.Bind(paneId, new SessionMcpContext(project, workingDirectory ?? Directory.GetCurrentDirectory(), confineRoot, agentName,
            assistantManaged ? SessionMcpManager.Assistant : SessionMcpManager.Operator)
        {
            Environment = environment is null ? _mounts.Set(paneId)?.Context?.Environment : StdioServerEnvironment.Build(environment),
        });
    }

    internal async Task PrepareLaunchAsync(string paneId, CancellationToken ct, bool waitForNames = true)
    {
        var token = _keyring.CurrentToken(paneId) ?? throw new InvalidOperationException("The MCP launch has no session key.");
        if (_provider?.Invoke() is { } provider && _mounts is not null)
        {
            var binding = _bindings.GetOrAdd((paneId, token), _ => new Lazy<GatewayMcpBinding>(() => new GatewayMcpBinding(provider, _mounts, paneId, token, _loggerFactory.CreateLogger<SessionMcpGatewayHost>()))).Value;
            if (waitForNames)
            {
                await binding.WaitForConnectionsAsync(ct).ConfigureAwait(false);
            }
            // ForwardTools registers reachable shortened names before launch-time glob rules are translated.
            _ = binding.Session.ForwardTools;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _StartAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            BoundUrl = null;
            _loggerFactory.CreateLogger<SessionMcpGatewayHost>().LogWarning(exception, "Session MCP gateway could not start; it is unavailable for this run.");
            try
            {
                await DisposeAsync();
            }
            catch (Exception cleanupException)
            {
                _loggerFactory.CreateLogger<SessionMcpGatewayHost>().LogWarning(cleanupException, "Session MCP gateway cleanup failed after startup failure.");
            }
        }
    }

    private async Task _StartAsync(CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateful;
#pragma warning disable MCP9006 // AC-2123 requires stateful sessions for list_changed notifications.
            options.IdleTimeout = TimeSpan.FromDays(30);
#pragma warning restore MCP9006
            options.ConfigureSessionOptions = async (http, session, ct) =>
            {
                var paneId = http.Request.RouteValues["paneId"]?.ToString()
                    ?? throw new InvalidOperationException("The gateway route has no pane.");
                var token = _Bearer(http);
                session.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
                GatewayMcpBinding? activeBinding = null;
                SemaphoreSlim? collectionGate = null;
                Action? refresh = null;
                var hasNativeSearch = false;
                var requiresSearchFallback = false;
                if (_provider?.Invoke() is { } provider && _mounts is not null)
                {
                    var binding = _bindings.GetOrAdd((paneId, token), _ => new Lazy<GatewayMcpBinding>(() => new GatewayMcpBinding(provider, _mounts, paneId, token, _loggerFactory.CreateLogger<SessionMcpGatewayHost>()))).Value;
                    if (!string.Equals(_keyring.CurrentToken(paneId), token, StringComparison.Ordinal))
                    {
                        _bindings.TryRemove((paneId, token), out _);
                        await binding.DisposeAsync();
                        throw new McpException("This MCP run has ended.");
                    }
                    activeBinding = binding;
                    var live = binding.Session;
                    var collection = session.ToolCollection;
                    var gate = new SemaphoreSlim(1);
                    var directTools = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
                    Action? exposeRefresh = null;
                    var searchTools = CockpitToolSearch.Build(() => live.GatewayTools).OfType<AIFunction>().Select(tool =>
                    {
                        var proxy = McpServerTool.Create(tool);
                        return tool.Name == CockpitToolSearch.CallToolName
                            ? new ForwardMcpServerTool(tool.Name, proxy.ProtocolTool, live, (request, token) => _CallFallbackAsync(paneId, live, request, name =>
                            {
                                if (directTools.TryAdd(name, 0))
                                {
                                    exposeRefresh?.Invoke();
                                }
                            }, token))
                            : proxy;
                    }).ToArray();
                    collectionGate = gate;
                    void Refresh()
                    {
                        gate.Wait();
                        try
                        {
                            var desired = live.ForwardTools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
                            if (!hasNativeSearch && (live.GatewayTools.Count > CockpitToolSearch.PreloadThreshold || requiresSearchFallback))
                            {
                                var preloaded = live.GatewayTools.Where(tool => tool.AlwaysMounted || directTools.ContainsKey(tool.Function.Name)).Select(tool => tool.Function.Name).ToHashSet(StringComparer.Ordinal);
                                desired = desired.Where(pair => preloaded.Contains(pair.Key)).ToDictionary(StringComparer.Ordinal);
                                foreach (var search in searchTools)
                                {
                                    desired[search.ProtocolTool.Name] = search;
                                }
                            }
                            using var batch = collection.DeferChangedEvents();
                            foreach (var existing in collection.ToArray())
                            {
                                if (!desired.TryGetValue(existing.ProtocolTool.Name, out var replacement)
                                    || (!ReferenceEquals(existing, replacement) && (existing is not ForwardMcpServerTool forward || !forward.Matches(replacement))))
                                {
                                    collection.Remove(existing);
                                }
                            }
                            foreach (var tool in desired.Values)
                            {
                                collection.TryAdd(tool);
                            }
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }
                    refresh = Refresh;
                    exposeRefresh = Refresh;
                    live.ToolsChanged += Refresh;
                    http.Items[typeof(GatewayMcpBinding)] = (Action)(() => live.ToolsChanged -= Refresh);
                    await binding.WaitForConnectionsAsync(ct);
                    session.ServerInstructions = live.CaptureServerInstructions();
                    Refresh();
                }
                session.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
                session.Filters = new McpServerFilters();
                session.Filters.Request.CallToolFilters.Add(next => async (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    if (activeBinding is not null && context.Params?.Name is { } name
                        && name is not (CockpitToolSearch.SearchToolName or CockpitToolSearch.CallToolName)
                        && !activeBinding.Session.ForwardTools.Any(tool => tool.ProtocolTool.Name == name))
                    {
                        return new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = "This tool is no longer available in this session's MCP set." }],
                        };
                    }
                    var result = await next(context, ct);
                    return McpInboxPiggyback.Attach(result, _inbox, _loggerFactory.CreateLogger<SessionMcpGatewayHost>());
                });
                session.Filters.Request.ListToolsFilters.Add(next => async (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    var clientName = context.Server.ClientInfo?.Name ?? string.Empty;
                    hasNativeSearch = clientName.Contains("claude", StringComparison.OrdinalIgnoreCase);
                    requiresSearchFallback = clientName.Contains("codex", StringComparison.OrdinalIgnoreCase);
                    if (activeBinding is null || collectionGate is null)
                    {
                        return await next(context, ct);
                    }
                    await activeBinding.WaitForConnectionsAsync(ct);
                    refresh?.Invoke();
                    await collectionGate.WaitAsync(ct);
                    try
                    {
                        return await next(context, ct);
                    }
                    finally
                    {
                        collectionGate.Release();
                    }
                });
            };
#pragma warning disable MCPEXP002 // Tie the SDK session to the existing run-key lifetime.
            options.RunSessionHandler = _RunSessionAsync;
#pragma warning restore MCPEXP002
        });

        var app = builder.Build();
        _app = app;
        McpAuthMiddleware.Require(app, _authKey, _keyring, _ => ValueTask.FromResult(true));
        app.Use(async (http, next) =>
        {
            var paneId = McpRequestContext.CurrentPaneId;
            var routePane = http.Request.RouteValues["paneId"]?.ToString();
            var sessionId = http.Request.Headers["Mcp-Session-Id"].ToString();
            if (paneId is null || paneId == NodeCallerIdentity.PaneId
                || !string.Equals(paneId, routePane, StringComparison.Ordinal)
                || (_owners.TryGetValue(sessionId, out var owner) && !string.Equals(owner, paneId, StringComparison.Ordinal)))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(http);
        });
        app.MapMcp("/s/{paneId}/mcp");
        _keyring.RunEnded += _EndBindings;
        CockpitConfigFileAccess.Written += _ConfigWritten;
        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        BoundUrl = addresses.Addresses.Single();
        _loggerFactory.CreateLogger<SessionMcpGatewayHost>().LogInformation("Session MCP gateway listening at {GatewayUrl}", BoundUrl);
    }

    private void _RequirePane(string paneId, string token)
    {
        if (!string.Equals(McpRequestContext.CurrentPaneId, paneId, StringComparison.Ordinal)
            || !string.Equals(_keyring.CurrentToken(paneId), token, StringComparison.Ordinal))
        {
            throw new McpException("This MCP session belongs to another pane.");
        }
    }

    private async Task _RunSessionAsync(HttpContext http, McpServer server, CancellationToken cancellationToken)
    {
        var paneId = http.Request.RouteValues["paneId"]?.ToString()
            ?? throw new InvalidOperationException("The gateway route has no pane.");
        var token = _Bearer(http);
        var sessionId = server.SessionId ?? throw new InvalidOperationException("The gateway session has no id.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var lifetimeLock = new Lock();
        var ended = false;
        void EndRun(string endedPane)
        {
            lock (lifetimeLock)
            {
                if (!ended && string.Equals(endedPane, paneId, StringComparison.Ordinal))
                {
                    _ = lifetime.CancelAsync();
                }
            }
        }

        _keyring.RunEnded += EndRun;
        _owners[sessionId] = paneId;
        try
        {
            if (!string.Equals(_keyring.CurrentToken(paneId), token, StringComparison.Ordinal))
            {
                return;
            }

            await server.RunAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            if (http.Items[typeof(GatewayMcpBinding)] is Action unsubscribe)
            {
                unsubscribe();
            }
            _owners.TryRemove(sessionId, out _);
            _keyring.RunEnded -= EndRun;
            lock (lifetimeLock)
            {
                ended = true;
            }
        }
    }

    private readonly ConcurrentDictionary<(string Pane, string Token), Lazy<GatewayMcpBinding>> _bindings = new();
    private readonly McpBackgroundTasks _cleanup;

    private void _ConfigWritten(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_configFile.ConfigFilePath), Cockpit.Core.WorkingPaths.DirectoryPath.Comparison))
        {
            _cleanup.Run(_RefreshProjectsAsync);
        }
    }

    private async Task _RefreshProjectsAsync()
    {
        if (_projects is null || _mounts is null)
        {
            return;
        }
        var projects = await _projects.LoadAsync();
        foreach (var paneId in _bindings.Keys.Select(key => key.Pane).Distinct(StringComparer.Ordinal))
        {
            if (_mounts.Set(paneId)?.Context is { } context)
            {
                _mounts.Bind(paneId, context with { Project = projects.Find(context.Project?.Id) });
            }
        }
    }

    private void _EndBindings(string paneId)
    {
        _toolAuthorization.TryRemove(paneId, out _);
        foreach (var pair in _bindings)
        {
            if (pair.Key.Pane == paneId && _bindings.TryRemove(pair.Key, out var binding) && binding.IsValueCreated)
            {
                _cleanup.Run(async () => await binding.Value.DisposeAsync());
            }
        }
    }

    private static string _Bearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : header;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        BoundUrl = null;
        if (_app is { } app)
        {
            await app.StopAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        BoundUrl = null;
        CockpitConfigFileAccess.Written -= _ConfigWritten;
        _keyring.RunEnded -= _EndBindings;
        if (_app is { } app)
        {
            _app = null;
            await app.DisposeAsync();
        }
        foreach (var binding in _bindings.Values)
        {
            if (binding.IsValueCreated) { await binding.Value.DisposeAsync(); }
        }
        _bindings.Clear();
        await _cleanup.DrainAsync();
    }
}
