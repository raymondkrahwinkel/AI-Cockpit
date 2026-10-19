using System.Net;
using System.Net.Http.Headers;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Plugins;

// AC-1587: what ICockpitHost.CreateAuthorizedHandler hands a plugin. The bearer goes on a copy of each request that
// the plugin never sees, and only to the origin of the plugin's own OAuth MCP server; the coordinator supplies and
// renews it exactly as it does for the MCP connection itself.
internal sealed class PluginMcpAuthorizedHandler(
    string serverName,
    Func<McpServerConfig?> resolveServer,
    IMcpOAuthCoordinator? coordinator,
    HttpMessageHandler upstream) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _upstream = new(upstream);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Resolved per request, so a connection whose URL changed is followed and a removed one is refused.
        var server = resolveServer()
            ?? throw new HttpRequestException($"'{serverName}' is not an OAuth MCP server this plugin contributed.");

        // Read once: the plugin still owns the request and could point it elsewhere while a credential is fetched.
        var target = request.RequestUri;
        if (!_IsOriginOf(server, target))
        {
            throw new HttpRequestException($"Refused: the request is not for the origin of MCP server '{serverName}'.");
        }

        if (coordinator is null)
        {
            return _Unauthorized(request);
        }

        var access = await _AskAsync(() => coordinator.AcquireAsync(server, interactive: false, cancellationToken)).ConfigureAwait(false);
        if (_TokenOf(access) is not { } accessToken)
        {
            return _Unauthorized(request);
        }

        var response = await _SendWithAsync(request, target, accessToken, cancellationToken).ConfigureAwait(false);

        // ponytail: only a request without a body is sent again, since content may not be readable twice; the
        // credential is renewed either way, so the plugin's own retry carries the new one.
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            var renewed = await _AskAsync(() => coordinator.RenewRejectedAsync(server, accessToken, cancellationToken)).ConfigureAwait(false);
            if (request.Content is null && _TokenOf(renewed) is { } renewedToken)
            {
                response.Dispose();
                response = await _SendWithAsync(request, target, renewedToken, cancellationToken).ConfigureAwait(false);
            }
        }

        // The upstream response points at the copy, which carries the bearer; the plugin only ever gets its own back.
        response.RequestMessage ??= request;
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _upstream.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<HttpResponseMessage> _SendWithAsync(HttpRequestMessage request, Uri? target, string accessToken, CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(request.Method, target)
        {
            Version = request.Version,
            VersionPolicy = request.VersionPolicy,
            Content = request.Content,
        };

        foreach (var header in request.Headers)
        {
            // Host is left out because SocketsHttpHandler takes it for the TLS server name and the virtual host, which
            // would let a plugin aim the bearer at another site behind the same address.
            if (!string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var option in request.Options)
        {
            ((IDictionary<string, object?>)copy.Options)[option.Key] = option.Value;
        }

        copy.Headers.Protocol = request.Headers.Protocol;
        copy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        try
        {
            return await _upstream.SendAsync(copy, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The content is the plugin's; disposing the copy must not take it along.
            copy.Content = null;
            copy.Dispose();
        }
    }

    // A credential failure can carry a fragment of the token exchange, so the plugin gets a fixed message instead.
    private static async Task<McpOAuthAccess> _AskAsync(Func<Task<McpOAuthAccess>> ask)
    {
        try
        {
            return await ask().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new HttpRequestException("The cockpit could not get a credential for this MCP server.");
        }
    }

    private static string? _TokenOf(McpOAuthAccess access) =>
        access.State == McpAuthState.Authorized && !string.IsNullOrWhiteSpace(access.AccessToken) ? access.AccessToken : null;

    private static HttpResponseMessage _Unauthorized(HttpRequestMessage request) =>
        new(HttpStatusCode.Unauthorized) { RequestMessage = request };

    private static bool _IsOriginOf(McpServerConfig server, Uri? target) =>
        target is { IsAbsoluteUri: true }
        && Uri.TryCreate(server.Url, UriKind.Absolute, out var origin)
        && string.Equals(_AsHttpScheme(target.Scheme), origin.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(target.IdnHost, origin.IdnHost, StringComparison.OrdinalIgnoreCase)
        && target.Port == origin.Port;

    // A WebSocket upgrade is addressed as ws/wss but travels as http/https; any other scheme stays itself and fails.
    private static string _AsHttpScheme(string scheme) => scheme switch
    {
        "ws" => Uri.UriSchemeHttp,
        "wss" => Uri.UriSchemeHttps,
        _ => scheme,
    };
}
