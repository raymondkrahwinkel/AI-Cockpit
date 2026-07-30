using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

/// <summary>
/// Forwards one loopback request to the OAuth-protected MCP server it stands for, swapping the local key on the way
/// in for a freshly obtained OAuth token (AC-524).
/// <para>
/// This is not an MCP implementation and must not become one. It relays a method, a path, a query, the headers and
/// the body, and streams the answer back; what any of it means is between the agent and the server. The single
/// exception is the reply it composes when there is no credential to forward with, which has to be shaped like an
/// answer the client will accept — see <see cref="_RespondUnavailableAsync"/> for why a 401 cannot be one.
/// </para>
/// </summary>
internal sealed class McpOAuthProxyForwarder(
    McpServerConfig server,
    IMcpOAuthCoordinator coordinator,
    HttpClient upstream,
    ILogger<McpOAuthProxyForwarder> logger)
{
    /// <summary>
    /// Headers that describe one hop of a connection rather than the message travelling over it (RFC 9110 §7.6.1),
    /// plus the framing the two servers each decide for themselves. Relaying these would have this proxy assert
    /// something about a connection it is not on.
    /// </summary>
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    /// <summary>
    /// JSON-RPC reserves −32000 to −32099 for implementation-defined server errors. The agent never acts on the
    /// number, only on the message, but a code outside the reserved band would be a claim about a standard meaning
    /// this error does not have.
    /// </summary>
    private const int CredentialUnavailableCode = -32001;

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // Buffered so the body can be read twice: once to forward, and — if the forward could not happen or came
        // back refused — once more to find the JSON-RPC id the reply has to carry. An MCP request is a small
        // JSON document, so this costs nothing worth avoiding.
        context.Request.EnableBuffering();

        var access = await coordinator.AcquireAsync(server, interactive: false, cancellationToken).ConfigureAwait(false);
        if (access.State != McpAuthState.Authorized || string.IsNullOrWhiteSpace(access.AccessToken))
        {
            await _RespondUnavailableAsync(context, access.Reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        HttpResponseMessage response;
        try
        {
            using var request = _BuildUpstreamRequest(context, access.AccessToken);
            response = await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The agent hung up. Nothing to say to anybody.
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Forwarding a request to MCP server {Server} failed.", server.Name);
            await _RespondUnavailableAsync(context, McpOAuthAttentionReason.ServerUnreachable, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            // A 401 is what the CLI reads as "this server is gone", and it drops the server and every tool on it for
            // the rest of the session — precisely the failure this proxy exists to prevent. So a rejected credential
            // is treated the same as one that could not be renewed: the connection is kept and the reason is said in
            // a form the agent can relay, which leaves the operator able to sign in again and the next call working.
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                logger.LogWarning(
                    "MCP server {Server} refused the cockpit's access token with {StatusCode}; the session is kept and the call is answered with the reason instead.",
                    server.Name,
                    (int)response.StatusCode);

                await _RespondUnavailableAsync(context, McpOAuthAttentionReason.SignInExpired, cancellationToken).ConfigureAwait(false);
                return;
            }

            await _RelayResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private HttpRequestMessage _BuildUpstreamRequest(HttpContext context, string accessToken)
    {
        // This endpoint stands for exactly one address, which is the address the config points the agent at — so the
        // upstream target is that address with whatever query came in, not a path rebuilt from the incoming request.
        var target = new UriBuilder(server.Url!);
        if (context.Request.QueryString.HasValue)
        {
            target.Query = context.Request.QueryString.Value!.TrimStart('?');
        }

        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target.Uri);

        // GET and DELETE are the two body-less methods MCP's streamable HTTP uses; giving them a StreamContent
        // anyway would put a Content-Length on a request that is defined not to have one.
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsDelete(context.Request.Method))
        {
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            // Authorization is the whole point of this hop: the local key that got the request in here is dropped and
            // the server's own credential put in its place. Host would name this loopback listener rather than the
            // server being addressed.
            if (HopByHopHeaders.Contains(header.Key)
                || string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Everything else travels untouched — Mcp-Session-Id, Mcp-Protocol-Version, Accept, Last-Event-ID and
            // whatever a future revision adds. A proxy that only relays the headers it recognises is a proxy that
            // breaks on the next version of the protocol.
            if (!request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value))
            {
                request.Content?.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string>)header.Value);
            }
        }

        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
        return request;
    }

    private static async Task _RelayResponseAsync(HttpContext context, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        context.Response.StatusCode = (int)response.StatusCode;

        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (!HopByHopHeaders.Contains(header.Key))
            {
                context.Response.Headers[header.Key] = new StringValues([.. header.Value]);
            }
        }

        // Kestrel decides its own framing for what goes out; a length copied from a response we are about to stream
        // chunk by chunk would be a promise this side cannot keep.
        context.Response.Headers.Remove("Content-Length");

        // MCP over streamable HTTP answers a request either with a JSON document or with an SSE stream that stays
        // open while the server keeps talking. Handing the body to CopyToAsync leaves the flushing to whoever owns
        // the buffer, and an event that is written but not yet flushed is an agent waiting on an answer that has
        // already been sent — the session simply hangs. Reading and flushing each chunk is what makes the first
        // event arrive when it was sent rather than when the response ends.
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (body.ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    /// <summary>
    /// The answer when there is no credential to forward with. Everything here is chosen so the client keeps the
    /// server rather than dropping it: the measured behaviour is that a 401 makes the CLI report the server as
    /// needing re-authorization and remove its tools from the session for good, and a session cannot get them back.
    /// So the request is answered as a request, with the reason where the agent will read it out loud, and the next
    /// call works the moment the operator has signed in again.
    /// </summary>
    private async Task _RespondUnavailableAsync(HttpContext context, McpOAuthAttentionReason reason, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            // Already streaming: there is no status left to set and no envelope that could be understood halfway
            // through a body. Cutting the connection is the only honest end.
            context.Abort();
            return;
        }

        var guidance = McpOAuthSignInGuidance.For(server.Name, reason);

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            // A GET here is the client opening the server-to-client stream. 405 is the answer the MCP transport
            // defines for a server that offers no such stream, so the client accepts it and carries on; a DELETE
            // (ending a session) has nothing to fail at. Neither is an authentication verdict, which is the point.
            context.Response.StatusCode = HttpMethods.IsGet(context.Request.Method)
                ? StatusCodes.Status405MethodNotAllowed
                : StatusCodes.Status202Accepted;
            return;
        }

        var id = await _ReadRequestIdAsync(context, cancellationToken).ConfigureAwait(false);
        if (id is null)
        {
            // No id means a notification, which by definition expects no reply. 202 is what the transport says to
            // answer one with, and inventing a response for it would be worse than saying nothing.
            context.Response.StatusCode = StatusCodes.Status202Accepted;
            return;
        }

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = CredentialUnavailableCode,
                ["message"] = $"Cockpit could not authenticate this call: {guidance}",
            },
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(envelope.ToJsonString(), cancellationToken).ConfigureAwait(false);
    }

    // The id of the JSON-RPC request being answered, or null when there is none to answer (a notification, a batch,
    // or a body that is not the shape this expects). Read from the buffered body, so it works both before the
    // request was forwarded and after it came back refused.
    private static async Task<JsonNode?> _ReadRequestIdAsync(HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            context.Request.Body.Position = 0;
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);

            // A response has to echo the request's id exactly, and JSON-RPC allows a string or a number. Anything
            // else is not an id this can answer, so it is treated as absent rather than guessed at.
            return body is JsonObject request && request.TryGetPropertyValue("id", out var id) && id is JsonValue value
                ? value.DeepClone()
                : null;
        }
        catch (JsonException)
        {
            // A body this cannot parse is a body whose id cannot be known. Nothing to report here: the caller
            // already logged why the request is being answered this way, and the answer is a 202 either way.
            return null;
        }
    }
}
