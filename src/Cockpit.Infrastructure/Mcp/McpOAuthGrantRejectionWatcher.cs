using System.Net;
using System.Text.Json;

namespace Cockpit.Infrastructure.Mcp;

// AC-646: watches the token endpoint for the one answer that says a refresh grant is really dead, from the SDK's
// own HttpClient since ClientOAuthProvider.RefreshTokensAsync discards the body on non-2xx. Only
// invalid_grant/invalid_client (RFC 6749 §5.2) count; everything else reports "could not be confirmed".
internal sealed class McpOAuthGrantRejectionWatcher : DelegatingHandler
{
    // How much of an error body is worth reading to find one word in it. An OAuth error response is a few dozen
    // bytes; anything past this is not one, and reading it on the strength of a remote server's say-so is not
    // something a credential path should do.
    private const long ErrorBodyLimit = 64 * 1024;

    private int _grantRejected;

    // Whether the token endpoint refused the refresh grant itself, as opposed to failing in some other way.
    public bool GrantRejected => Volatile.Read(ref _grantRejected) != 0;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Decided before sending, because afterwards the SDK owns the request's lifetime.
        var isRefresh = await _IsRefreshGrantAsync(request, cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        if (isRefresh && await _RefusesTheGrantAsync(response, cancellationToken).ConfigureAwait(false))
        {
            Volatile.Write(ref _grantRejected, 1);
        }

        return response;
    }

    private static async Task<bool> _IsRefreshGrantAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Content type is checked before reading, since every MCP message shares this client — a JSON-RPC call
        // must not have its body pulled into a string on the way past.
        if (request.Method != HttpMethod.Post
            || request.Content?.Headers.ContentType?.MediaType != "application/x-www-form-urlencoded")
        {
            return false;
        }

        var form = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return form.Contains("grant_type=refresh_token", StringComparison.Ordinal);
    }

    private static async Task<bool> _RefusesTheGrantAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // 400 and 401 are the only statuses RFC 6749 §5.2 gives these two errors. A 5xx carrying an `error` field
        // anyway would be a server contradicting itself, and the reading that costs nothing is the cautious one.
        if (response.StatusCode is not (HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized))
        {
            return false;
        }

        try
        {
            // Buffered with a ceiling rather than trusted to be small: this is a remote server's body, and it is read
            // on nothing but its own say-so. Over the limit throws, which lands in the same answer as unreadable.
            await response.Content.LoadIntoBufferAsync(ErrorBodyLimit, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is "invalid_grant" or "invalid_client";
        }
        catch (Exception exception) when (exception is JsonException or HttpRequestException or IOException)
        {
            // A body that cannot be read or parsed is a body that said nothing. Nothing to log: the caller already
            // reports the failure, and this only decides which of two sentences it uses.
            return false;
        }
    }
}
