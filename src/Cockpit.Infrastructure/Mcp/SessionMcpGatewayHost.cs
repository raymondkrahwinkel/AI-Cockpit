using System.Collections.Concurrent;
using System.Net;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;
using Cockpit.Core.Abstractions.Agents;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cockpit.Infrastructure.Mcp;

internal sealed class SessionMcpGatewayHost : IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly ILoggerFactory _loggerFactory;
    private readonly CockpitConfigFileAccess _configFile;
    private readonly Func<McpToolProvider?>? _provider;
    private readonly SessionMcpMounts? _mounts;
    private readonly IAgentTurnInboxDelivery? _inbox;
    private WebApplication? _app;

    public SessionMcpGatewayHost(McpAuthKey authKey, SessionMcpKeyring keyring, ILoggerFactory loggerFactory, Func<IMcpToolProvider>? provider = null, SessionMcpMounts? mounts = null, IAgentTurnInboxDelivery? inbox = null)
        : this(authKey, keyring, loggerFactory, CockpitConfigPath.Default, mounts: mounts, inbox: inbox)
    {
        _provider = () => provider?.Invoke() as McpToolProvider;
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
    }
    internal string? BoundUrl { get; private set; }
    public bool IsEnabled { get; private set; }

    public string? UrlFor(string paneId) => IsEnabled && BoundUrl is { } url
        ? $"{url}/s/{Uri.EscapeDataString(paneId)}/mcp"
        : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            IsEnabled = (await _configFile.ReadAsync(cancellationToken))?.SessionMcpGatewayEnabled ?? false;
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
                if (_provider?.Invoke() is { } provider && _mounts is not null)
                {
                    var binding = _bindings.GetOrAdd((paneId, token), _ => new Lazy<GatewayMcpBinding>(() => new GatewayMcpBinding(provider, _mounts, paneId, token))).Value;
                    var live = binding.Session;
                    var collection = session.ToolCollection;
                    void Refresh()
                    {
                        lock (collection)
                        {
                            collection.Clear();
                            foreach (var tool in live.ForwardTools)
                            {
                                collection.Add(tool);
                            }
                        }
                    }
                    live.ToolsChanged += Refresh;
                    http.Items[typeof(GatewayMcpBinding)] = (Action)(() => live.ToolsChanged -= Refresh);
                    await Task.WhenAny(binding.Initial, Task.Delay(TimeSpan.FromSeconds(3), ct));
                    session.ServerInstructions = live.ServerInstructions;
                    Refresh();
                }
                session.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
                session.Filters = new McpServerFilters();
                session.Filters.Request.CallToolFilters.Add(next => async (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    var result = await next(context, ct);
                    return McpInboxPiggyback.Attach(result, _inbox, _loggerFactory.CreateLogger<SessionMcpGatewayHost>());
                });
                session.Filters.Request.ListToolsFilters.Add(next => (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    return next(context, ct);
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
    private readonly ConcurrentBag<Task> _cleanup = [];

    private void _EndBindings(string paneId)
    {
        foreach (var pair in _bindings)
        {
            if (pair.Key.Pane == paneId && _bindings.TryRemove(pair.Key, out var binding) && binding.IsValueCreated)
            {
                _cleanup.Add(binding.Value.DisposeAsync().AsTask());
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
        await Task.WhenAll(_cleanup);
    }
}
