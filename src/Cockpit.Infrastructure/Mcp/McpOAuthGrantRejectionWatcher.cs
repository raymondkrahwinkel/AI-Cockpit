using System.Net;
using System.Text.Json;

namespace Cockpit.Infrastructure.Mcp;

// Watches the token endpoint for the one answer that says a refresh grant is really dead (AC-646).
//
// It has to be watched from out here because the SDK throws the answer away: `ClientOAuthProvider.RefreshTokensAsync`
// returns null on any non-2xx without ever reading the body, and the flow then falls through to an authorization
// step that refuses non-interactively — so a revoked grant and a token endpoint that was merely having a bad minute
// arrive at the caller as the same exception. Sitting on the transport's `HttpClient`, which is the same one the SDK
// hands its OAuth provider, is what makes the difference visible without a second OAuth implementation growing
// alongside the SDK's.
//
// Only `invalid_grant` and `invalid_client` count (RFC 6749 §5.2): those are an authorization server saying the
// grant will never work again. Everything else it can answer with — a 5xx, a rate limit, a body that is not the
// shape this expects — leaves this false, and the caller reports that it could not be confirmed rather than
// claiming the sign-in is gone.
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
        // Read before sending: afterwards the SDK owns the request's lifetime. Safe to read twice — the SDK builds
        // this body as `FormUrlEncodedContent`, which is a byte array and not a stream that a read consumes.
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
        if (request.Method != HttpMethod.Post || request.Content is null)
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
