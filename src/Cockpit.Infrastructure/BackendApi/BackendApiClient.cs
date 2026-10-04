using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Cockpit.Core.Abstractions.Events;
using Cockpit.Infrastructure.Mcp;

namespace Cockpit.Infrastructure.BackendApi;

public sealed class BackendApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // AC-1456: the events route pings every 15 s, so three missed pings is a connection that died without a reset.
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(45);
    private readonly HttpClient _http;
    private readonly TimeProvider _time;

    public BackendApiClient(Uri baseAddress, string key, string fingerprint, TimeProvider time)
    {
        if (!string.Equals(baseAddress.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The backend API base address must use HTTPS.", nameof(baseAddress));
        }

        _time = time;
        _http = new HttpClient(NodeCertificatePin.Require(fingerprint))
        {
            BaseAddress = baseAddress,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<BackendWhoAmI> WhoAmIAsync(CancellationToken cancellationToken = default) =>
        _SendAsync<BackendWhoAmI>(HttpMethod.Get, "api/v1/whoami", null, cancellationToken);

    public Task<T> GetAsync<T>(string path) => _SendAsync<T>(HttpMethod.Get, path, null, CancellationToken.None);

    public Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) =>
        _SendAsync<T>(HttpMethod.Get, path, null, cancellationToken);

    public Task<T> SendAsync<T>(HttpMethod method, string path, object? body) =>
        _SendAsync<T>(method, path, body, CancellationToken.None);

    public Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        _SendAsync<T>(method, path, body, cancellationToken);

    // AC-1446: for a route that writes its enums as words, as the connect-key routes do.
    public Task<T> SendAsync<T>(HttpMethod method, string path, object? body, JsonSerializerOptions options, CancellationToken cancellationToken) =>
        _SendAsync<T>(method, path, body, cancellationToken, options);

    // AC-1456: `connected` hears true when a connection's headers came back and false each time one ended or failed.
    public async IAsyncEnumerable<BackendEvent> StreamEventsAsync(
        long? afterSeq,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Action<bool>? connected = null)
    {
        var cursor = afterSeq;
        var backoff = TimeSpan.FromSeconds(1);
        while (true)
        {
            await using var events = _ReadConnectionAsync(cursor, () => connected?.Invoke(true), cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool moved;
                try
                {
                    moved = await events.MoveNextAsync().ConfigureAwait(false);
                }
                catch (BackendApiException exception) when (exception.Status is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    throw;
                }
                catch (BackendApiException)
                {
                    break;
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException)
                {
                    break;
                }

                if (!moved)
                {
                    break;
                }

                var evt = events.Current;
                cursor = evt.Seq;
                backoff = TimeSpan.FromSeconds(1);
                yield return evt;
            }

            connected?.Invoke(false);
            await Task.Delay(backoff, _time, cancellationToken).ConfigureAwait(false);
            backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<T> _SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        JsonSerializerOptions? options = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: options ?? Json);
        }

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await _ErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var value = await response.Content.ReadFromJsonAsync<T>(options ?? Json, cancellationToken).ConfigureAwait(false);
        return value is null ? throw new JsonException("The backend API returned an empty JSON response.") : value;
    }

    private async IAsyncEnumerable<BackendEvent> _ReadConnectionAsync(
        long? cursor,
        Action opened,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/events");
        if (cursor is { } lastEventId)
        {
            request.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId.ToString(CultureInfo.InvariantCulture));
        }

        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await _ErrorAsync(response, cancellationToken).ConfigureAwait(false);
        }

        opened();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        string? id = null;
        string? kind = null;
        var data = new List<string>();
        while (true)
        {
            silence.CancelAfter(SilenceLimit);
            string? line;
            try
            {
                line = await reader.ReadLineAsync(silence.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException("The event stream stayed silent past three heartbeats.");
            }

            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                var evt = _Frame(id, kind, data);
                id = null;
                kind = null;
                data.Clear();
                if (evt is { } frame)
                {
                    yield return frame;
                }

                continue;
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? "" : line[(separator + 1)..].TrimStart(' ');
            switch (field)
            {
                case "id":
                    id = value;
                    break;
                case "event":
                    kind = value;
                    break;
                case "data":
                    data.Add(value);
                    break;
            }
        }
    }

    private static BackendEvent? _Frame(string? id, string? kind, IReadOnlyList<string> data)
    {
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            || string.IsNullOrEmpty(kind)
            || data.Count == 0)
        {
            return null;
        }

        using var document = JsonDocument.Parse(string.Join('\n', data));
        var payload = document.RootElement.Clone();
        // The events bridge writes `PaneId`; either spelling names the pane.
        var paneId = payload.ValueKind == JsonValueKind.Object
            && (payload.TryGetProperty("PaneId", out var pane) || payload.TryGetProperty("paneId", out pane))
            && pane.ValueKind == JsonValueKind.String
                ? pane.GetString()
                : null;
        return new BackendEvent(seq, kind, paneId, payload);
    }

    private static async Task<BackendApiException> _ErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var code = response.ReasonPhrase ?? "http_error";
        var description = code;
        try
        {
            using var document = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                && error.GetString() is { } parsedCode)
            {
                code = parsedCode;
            }

            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error_description", out var errorDescription)
                && errorDescription.ValueKind == JsonValueKind.String
                && errorDescription.GetString() is { } parsedDescription)
            {
                description = parsedDescription;
            }
        }
        catch (JsonException)
        {
        }

        return new BackendApiException(response.StatusCode, code, description);
    }
}

// AC-1458: the last four are additive, as are AC-1456's holder and AC-1469's grant; a server that predates them leaves
// them at their defaults, so a key it says nothing of is shown without answer buttons.
public sealed record BackendWhoAmI(
    string KeyPrefix,
    string Label,
    string Capability,
    string Node,
    int ApiVersion,
    DateTimeOffset? ExpiresAt = null,
    bool HoldsAssistant = false,
    string? Version = null,
    DateTimeOffset? StartedAt = null,
    string? AssistantHeldBy = null,
    bool MayAnswerPermissions = false);
