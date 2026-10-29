using System.Net;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes;

// AC-1581: GET /cockpit/note-link/{token} (DEP-534), the one place a token becomes a note. Depot answers 404 notes.unavailable
// for no right, a foreign organization, a bad token and a deleted note alike, so this class cannot tell them apart either.
internal sealed class DepotNoteLinks(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    ILogger? logger = null)
{
    public async Task<DepotNoteLinkAnswer> ResolveAsync(DepotNoteLinkRequest request, CancellationToken cancellationToken)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == request.ConnectionId);
        if (connection is null)
        {
            return new(DepotNoteLinkState.SignIn);
        }

        if (!DepotNoteToken.IsValid(request.Token))
        {
            return new(DepotNoteLinkState.Unavailable);
        }

        try
        {
            using var http = new HttpClient(handlerFor(connection)) { Timeout = TimeSpan.FromSeconds(20) };
            using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/note-link/{request.Token}", cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return response.StatusCode switch
            {
                HttpStatusCode.OK => _Parse(body),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(DepotNoteLinkState.SignIn),
                HttpStatusCode.NotFound when _Code(body) == "notes.unavailable" => new(DepotNoteLinkState.Unavailable),
                _ => new(DepotNoteLinkState.Unreachable),
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException
            || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            logger?.LogWarning(exception, "Resolving a note link on {Connection} failed.", connection.Name);
            return new(DepotNoteLinkState.Unreachable);
        }
    }

    private static DepotNoteLinkAnswer _Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        string? Field(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return Field("org") is { Length: > 0 } organization && Field("project") is { Length: > 0 } project && Field("path") is { Length: > 0 } path
            ? new(DepotNoteLinkState.Ok, organization, project, path, Field("title"), Field("role"))
            : new(DepotNoteLinkState.Unreachable);
    }

    private static string? _Code(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
