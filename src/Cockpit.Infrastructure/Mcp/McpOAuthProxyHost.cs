using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;
using Cockpit.Core.Sessions;

namespace Cockpit.Infrastructure.Mcp;

// AC-524: one loopback listener per OAuth-protected server, standing in for its real address, forwarding every
// request upstream with a fresh OAuth token. Its own host rather than a CockpitMcpEndpointHost mode, sharing
// only the auth gate; a listener binds on first use and stays up for the app's lifetime.
internal sealed class McpOAuthProxyHost : IMcpOAuthProxy, ISingletonService, IAsyncDisposable
{
    private readonly IMcpOAuthCoordinator _coordinator;
    private readonly McpAuthKey _authKey;
    private readonly SessionMcpKeyring _keyring;
    private readonly SessionMcpMounts _mounts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpOAuthProxyHost> _logger;
    private readonly ConcurrentDictionary<string, string> _mounted = new(StringComparer.Ordinal);
    private readonly List<WebApplication> _apps = [];

    // One gate per server, not a global one — a launch's shared mount budget (five seconds on the TTY route)
    // would otherwise have a slow first mount spend the second server's time too.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _mountGates = new(StringComparer.Ordinal);

    // One client per proxied server with no timeout — the default hundred seconds would cut an SSE stream off
    // mid-session. Redirects are not followed, since that's the upstream's answer to relay, not ours to resolve.
    private readonly HttpClient _upstream = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public McpOAuthProxyHost(
        IMcpOAuthCoordinator coordinator,
        McpAuthKey authKey,
        SessionMcpKeyring keyring,
        SessionMcpMounts mounts,
        ILoggerFactory loggerFactory)
    {
        _coordinator = coordinator;
        _authKey = authKey;
        _keyring = keyring;
        _mounts = mounts;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpOAuthProxyHost>();
    }

    public async Task<string?> MountAsync(McpServerConfig server, CancellationToken cancellationToken = default)
    {
        // The scope of this whole feature, checked in one place: nothing else changes behaviour for a server that is
        // not an OAuth-protected HTTP endpoint, because for those this simply has nothing to offer.
        if (server.Auth != McpServerAuth.OAuth
            || server.Transport != McpTransport.Http
            || !Uri.TryCreate(server.Url, UriKind.Absolute, out var upstreamUrl))
        {
            return null;
        }

        // Keyed on the address as well as the id: an operator who edits the URL under a server is pointing at a
        // different host, and a listener still forwarding to the old one would send this session somewhere it no
        // longer belongs.
        var key = $"{server.IdentityKey}\n{upstreamUrl.AbsoluteUri}";

        // Checked before taking the gate as well as after: once a listener is up, every later session finds it here
        // without queueing behind anything at all.
        if (_mounted.TryGetValue(key, out var alreadyListening))
        {
            return alreadyListening;
        }

        var gate = _mountGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_mounted.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var url = await _StartListenerAsync(server, cancellationToken).ConfigureAwait(false);
            _mounted[key] = url;

            _logger.LogInformation(
                "MCP server {Server} is reached through a cockpit loopback endpoint at {ProxyUrl}; its OAuth token is renewed per request and no longer written into a session's config.",
                server.Name,
                url);

            return url;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller put a bound on how long a launch waits. Reporting that as "could not mount" would be a
            // guess about a listener that may well have come up a moment later.
            throw;
        }
        catch (Exception exception)
        {
            // Degraded rather than broken: the session falls back to the token being written into its config with
            // the wider session margin, which is where this feature started. Said out loud, because the difference
            // between the two is whether the session survives its token expiring.
            _logger.LogWarning(
                exception,
                "Could not open the loopback endpoint for OAuth MCP server {Server}; this session falls back to writing its access token into the config, which will stop working when that token expires.",
                server.Name);

            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> _StartListenerAsync(McpServerConfig server, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.AddSingleton(_loggerFactory);
        builder.WebHost.UseKestrel();
        // Port 0: the OS picks a free loopback port, so nothing to configure and no collision with a second cockpit.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var app = builder.Build();

        // The same gate the cockpit's own endpoints use (AC-40), so no local process can borrow the OAuth
        // credential by finding the port. AC-1148: and the token must be a session that mounted this very server.
        // No master switch of its own — an OAuth server the operator disabled is never proxied in the first place.
        McpAuthMiddleware.Require(
            app,
            _authKey,
            _keyring,
            paneId => new ValueTask<bool>(McpEndpointAuthorization.Allows(paneId, server.Name, isEnabled: true, nodeScopeGranted: false, _mounts)));

        var forwarder = new McpOAuthProxyForwarder(
            server,
            _coordinator,
            _upstream,
            _loggerFactory.CreateLogger<McpOAuthProxyForwarder>());

        // A typed local rather than a lambda inline: WebApplication carries both Run(string) and the terminal
        // Run(RequestDelegate), and the overload has to be the second one.
        RequestDelegate forward = context => forwarder.ForwardAsync(context, context.RequestAborted);
        app.Run(forward);

        // Registered for teardown only once it is actually running, and torn down here if it is not. A start that
        // was cancelled or refused would otherwise leave a half-bound listener behind with nothing in _mounted
        // pointing at it: unreachable, unstoppable, and holding a port the next attempt cannot reuse.
        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _apps.Add(app);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel did not expose its bound addresses.");
        var boundUrl = addresses.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException($"The loopback endpoint for MCP server {server.Name} bound no address.");

        return $"{boundUrl.TrimEnd('/')}/mcp";
    }

    public async ValueTask DisposeAsync()
    {
        // The listeners go first, and only then the things a request in flight is still holding: disposing the
        // client or a gate while a relay is mid-stream would end that request on an ObjectDisposedException, and a
        // mount that is running would throw on a gate that no longer exists.
        foreach (var app in _apps)
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var gate in _mountGates.Values)
        {
            gate.Dispose();
        }

        _upstream.Dispose();
    }
}
