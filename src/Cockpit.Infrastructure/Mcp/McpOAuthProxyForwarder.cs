using System.Buffers;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Cockpit.Core.Abstractions.Mcp;
using Cockpit.Core.Mcp;

namespace Cockpit.Infrastructure.Mcp;

// AC-524: forwards one loopback request to the OAuth-protected server it stands for, swapping the local key for a
// freshly obtained OAuth token. Not an MCP implementation and must not become one — it relays everything as-is,
// except the reply it composes when there is no credential to forward with (see `_RespondUnavailableAsync`).
internal sealed class McpOAuthProxyForwarder(
    McpServerConfig server,
    IMcpOAuthCoordinator coordinator,
    HttpClient upstream,
    ILogger<McpOAuthProxyForwarder> logger)
{
    // Headers that describe one hop of a connection rather than the message travelling over it (RFC 9110 §7.6.1),
    // plus the framing the two servers each decide for themselves. Relaying these would have this proxy assert
    // something about a connection it is not on.
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer",
        "Transfer-Encoding", "Upgrade",
    };

    // JSON-RPC reserves −32000 to −32099 for implementation-defined server errors. The agent never acts on the
    // number, only on the message, but a code outside the reserved band would be a claim about a standard meaning
    // this error does not have.
    private const int CredentialUnavailableCode = -32001;

    // How much of a request body is kept in memory before the buffer spills to a temp file. An MCP call is
    // a JSON document and almost always fits; the threshold is generous so the ordinary one never touches disk.
    private const int MemoryBufferThreshold = 128 * 1024;

    // Above this, the body only exists as a temp file and re-reading it to save one retry is a poor trade — a
    // limit on the retry only, the first forward is never refused for size.
    private const long RepeatableBodyLimit = 8L * 1024 * 1024;

    // How long to wait before asking for a credential a second time. Long enough that a rotation race or a token
    // endpoint's bad second has passed, short enough to disappear inside a tool call the agent is already waiting on.
    private static readonly TimeSpan RenewalSecondChanceDelay = TimeSpan.FromMilliseconds(500);

    public async Task ForwardAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // Buffered so the body can be read more than once: to forward it, to retry after a renewed credential, and
        // to find the JSON-RPC id a refusal is answered under. No hard limit — that would abort the ordinary
        // forward too, and refusing a large call outright is worse than relaying it and only losing its retry.
        context.Request.EnableBuffering(bufferThreshold: MemoryBufferThreshold);

        var access = await _AcquireWithOneSecondChanceAsync(cancellationToken).ConfigureAwait(false);
        if (access.State != McpAuthState.Authorized || string.IsNullOrWhiteSpace(access.AccessToken))
        {
            await _RespondUnavailableAsync(context, access.Reason, cancellationToken).ConfigureAwait(false);
            return;
        }

        var response = await _SendAsync(context, access.AccessToken, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return;
        }

        try
        {
            if (_IsRefusal(response))
            {
                var retried = await _RetryWithARenewedCredentialAsync(context, access.AccessToken, response, cancellationToken).ConfigureAwait(false);
                if (retried is null)
                {
                    return;
                }

                response = retried;
            }

            await _RelayResponseAsync(context, response, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The agent hung up mid-stream. Ordinary — a session that stops reading is how a cancelled tool call
            // ends — and there is nobody left to tell.
        }
        catch (Exception exception)
        {
            // The far side broke while its answer was already going out. Nothing can be said in the response any
            // more, so this exists to make sure it is not said nowhere: every other departure in this class is
            // logged, and a stream that ends in a dropped connection is what the CLI reads as "server gone".
            logger.LogWarning(exception, "The response from MCP server {Server} broke off while it was being relayed.", server.Name);
            _AbortIfStarted(context);
        }
        finally
        {
            response.Dispose();
        }
    }

    // AC-646: the credential for this call, with one second chance a failed silent renewal gets — measured, a
    // renewal that failed once succeeded moments later. Only for unconfirmed/unreachable, never a dead grant;
    // safe on the shared path since the coordinator's single-flight table coalesces concurrent second chances.
    private async Task<McpOAuthAccess> _AcquireWithOneSecondChanceAsync(CancellationToken cancellationToken)
    {
        var access = await coordinator.AcquireAsync(server, interactive: false, cancellationToken).ConfigureAwait(false);
        if (access.Reason is not (McpOAuthAttentionReason.RenewalCouldNotBeConfirmed or McpOAuthAttentionReason.ServerUnreachable))
        {
            return access;
        }

        logger.LogInformation(
            "Renewing the authorization for MCP server {Server} did not succeed ({Reason}); asking once more before this call is given up.",
            server.Name,
            access.Reason);

        await Task.Delay(RenewalSecondChanceDelay, cancellationToken).ConfigureAwait(false);
        return await coordinator.AcquireAsync(server, interactive: false, cancellationToken).ConfigureAwait(false);
    }

    // The one retry a refused credential gets — the server alone knows for certain a grant is dead, so a single
    // retry avoids losing the server over one renewal. Never a loop; the coordinator coalesces concurrent retries.
    // Returns the response to relay, or `null` when this method has already answered the request.
    private async Task<HttpResponseMessage?> _RetryWithARenewedCredentialAsync(
        HttpContext context,
        string rejectedAccessToken,
        HttpResponseMessage refusal,
        CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "MCP server {Server} refused the cockpit's access token with {StatusCode}; renewing it and sending the call once more.",
            server.Name,
            (int)refusal.StatusCode);

        var renewed = await coordinator.RenewRejectedAsync(server, rejectedAccessToken, cancellationToken).ConfigureAwait(false);
        if (renewed.State != McpAuthState.Authorized || string.IsNullOrWhiteSpace(renewed.AccessToken))
        {
            await _RespondUnavailableAsync(context, renewed.Reason, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (!_CanSendAgain(context))
        {
            // The credential is renewed either way, so the next call works; only this one is lost. Saying that is
            // the point — an agent told "send it again" acts on it, where a bare failure only gets repeated blindly.
            logger.LogWarning(
                "The call to MCP server {Server} was too large to send again after its credential was renewed; the credential is now current and the next call will carry it.",
                server.Name);

            await _RespondUnavailableAsync(context, McpOAuthAttentionReason.CallCouldNotBeRepeated, cancellationToken).ConfigureAwait(false);
            return null;
        }

        refusal.Dispose();
        context.Request.Body.Position = 0;

        var second = await _SendAsync(context, renewed.AccessToken, cancellationToken).ConfigureAwait(false);
        if (second is null)
        {
            return null;
        }

        // AC-550: still refused with a credential minted seconds ago — renewing again would find the same answer,
        // so this stops and tells the operator instead of guessing "expired", which measured evidence rules out.
        if (_IsRefusal(second))
        {
            logger.LogWarning(
                "MCP server {Server} refused a freshly renewed access token as well ({StatusCode}); the session is kept and the call is answered with the reason instead.",
                server.Name,
                (int)second.StatusCode);

            second.Dispose();
            await _RespondUnavailableAsync(context, McpOAuthAttentionReason.RenewedCredentialRefused, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return second;
    }

    // Sends one attempt upstream, or answers the request itself and returns `null`.
    private async Task<HttpResponseMessage?> _SendAsync(HttpContext context, string accessToken, CancellationToken cancellationToken)
    {
        try
        {
            using var request = _BuildUpstreamRequest(context, accessToken);
            return await upstream.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The agent hung up. Nothing to say to anybody.
            return null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Forwarding a request to MCP server {Server} failed.", server.Name);
            await _RespondUnavailableAsync(context, McpOAuthAttentionReason.ServerUnreachable, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }

    // A 401 is what the CLI reads as "this server is gone" — it drops the server and every tool on it for the rest
    // of the session. A 403 travels with it because a server that scopes its tokens answers a stale one that way.
    private static bool _IsRefusal(HttpResponseMessage response) =>
        response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    // Whether the request body can go out a second time. It can whenever the buffering above kept it, which is
    // every call up to the limit; past that the buffer is a temp file that would have to be re-read from disk for
    // a retry whose only purpose is to save one call, and the honest answer is to say the call was lost instead.
    private static bool _CanSendAgain(HttpContext context) =>
        context.Request.Body.CanSeek && (context.Request.ContentLength ?? 0) <= RepeatableBodyLimit;

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
            // Wrapped so the send cannot close the buffered body: HttpClient disposes the content it was given, and
            // with it the stream inside — which would take away the one copy of the request that is left to read the
            // JSON-RPC id from when the answer comes back refused.
            request.Content = new StreamContent(new BorrowedStream(context.Request.Body));
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

        // An SSE stream can stay open indefinitely; CopyToAsync would leave flushing to whoever owns the buffer,
        // hanging the agent on an event that was written but not yet flushed. Read-and-flush per chunk instead.
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

    // The answer when there's no credential to forward with, chosen so the client keeps the server rather than
    // dropping it — a bare 401 makes the CLI remove the server's tools for the rest of the session, for good.
    private async Task _RespondUnavailableAsync(HttpContext context, McpOAuthAttentionReason reason, CancellationToken cancellationToken)
    {
        if (context.Response.HasStarted)
        {
            // Already streaming: there is no status left to set and no envelope that could be understood halfway
            // through a body. Cutting the connection is the only honest end.
            _AbortIfStarted(context);
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

    // Ends a response that is already on the wire. There is no status code left to set and no envelope a client
    // could make sense of halfway through a body, so cutting it is the only ending left; a response that has not
    // started yet is still answerable and must not be cut.
    private static void _AbortIfStarted(HttpContext context)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
        }
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
        catch (ObjectDisposedException)
        {
            // The request stream is gone, so there is no id to echo and nothing to be done about it. Not a reason to
            // fail the response: 202 without an id is still better than the 401 this method exists to avoid.
            return null;
        }
    }

    // Lends a stream out without lending out the right to close it. `HttpClient` disposes the content of
    // every request it sends, and this request's content is the caller's own buffered body — which still has to be
    // readable afterwards.
    private sealed class BorrowedStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        // The whole point of the class: the borrower's Dispose does nothing, and the owner closes it when the
        // request ends.
        protected override void Dispose(bool disposing)
        {
        }
    }
}
