using System.Collections.Concurrent;
using System.Net;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Mcp;
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

internal sealed class SessionMcpGatewayHost(
    McpAuthKey authKey,
    SessionMcpKeyring keyring,
    ISessionBehaviorSettingsStore settings,
    ILoggerFactory loggerFactory) : IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);
    private WebApplication? _app;
    internal string? BoundUrl { get; private set; }
    public bool IsEnabled { get; private set; }

    public string? UrlFor(string paneId) => IsEnabled && BoundUrl is { } url
        ? $"{url}/s/{Uri.EscapeDataString(paneId)}/mcp"
        : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        IsEnabled = (await settings.LoadAsync(cancellationToken)).SessionMcpGatewayEnabled;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(loggerFactory);
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddMcpServer().WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateful;
#pragma warning disable MCP9006 // AC-2123 requires stateful sessions for list_changed notifications.
            options.IdleTimeout = TimeSpan.FromDays(30);
#pragma warning restore MCP9006
            options.ConfigureSessionOptions = (http, session, _) =>
            {
                var paneId = http.Request.RouteValues["paneId"]?.ToString()
                    ?? throw new InvalidOperationException("The gateway route has no pane.");
                var token = _Bearer(http);
                session.ToolCollection = new McpServerPrimitiveCollection<McpServerTool>();
                session.Capabilities = new ServerCapabilities { Tools = new ToolsCapability { ListChanged = true } };
                session.Filters = new McpServerFilters();
                session.Filters.Request.CallToolFilters.Add(next => (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    return next(context, ct);
                });
                session.Filters.Request.ListToolsFilters.Add(next => (context, ct) =>
                {
                    _RequirePane(paneId, token);
                    return next(context, ct);
                });
                return Task.CompletedTask;
            };
#pragma warning disable MCPEXP002 // Tie the SDK session to the existing run-key lifetime.
            options.RunSessionHandler = _RunSessionAsync;
#pragma warning restore MCPEXP002
        });

        var app = builder.Build();
        _app = app;
        McpAuthMiddleware.Require(app, authKey, keyring, _ => ValueTask.FromResult(true));
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
        await app.StartAsync(cancellationToken);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        BoundUrl = addresses.Addresses.Single();
    }

    private void _RequirePane(string paneId, string token)
    {
        if (!string.Equals(McpRequestContext.CurrentPaneId, paneId, StringComparison.Ordinal)
            || !string.Equals(keyring.CurrentToken(paneId), token, StringComparison.Ordinal))
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
                    lifetime.Cancel();
                }
            }
        }

        keyring.RunEnded += EndRun;
        _owners[sessionId] = paneId;
        try
        {
            if (!string.Equals(keyring.CurrentToken(paneId), token, StringComparison.Ordinal))
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
            _owners.TryRemove(sessionId, out _);
            keyring.RunEnded -= EndRun;
            lock (lifetimeLock)
            {
                ended = true;
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
        if (_app is { } app)
        {
            _app = null;
            await app.DisposeAsync();
        }
    }
}
