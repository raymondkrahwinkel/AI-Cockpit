using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Fetches open ("#Unresolved") issues for a single YouTrack project over a plain <see cref="HttpClient"/>,
/// authenticated with a permanent token — YouTrack has no local CLI equivalent to <c>gh</c>, so this plugin
/// is HTTP-only. Mirrors the query shape from the YouTrack skill: <c>GET {instance}/issues?fields=…&amp;
/// query=project:{tag} #Unresolved [extra]&amp;$top={n}</c>. Callers are expected to validate that the
/// instance URL, token and project tag are set before calling — this client assumes valid input and lets
/// HTTP/JSON failures surface as exceptions for the UI layer to report.
/// </summary>
internal sealed class YouTrackClient
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<YouTrackIssue>> GetOpenIssuesAsync(string instanceBaseUrl, string token, string projectTag, string? extraFilter, int top, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var query = BuildQuery(projectTag, extraFilter);
        var url = $"{baseUrl}/issues?fields=idReadable,id,summary,description,customFields(name,value(name))&query={Uri.EscapeDataString(query)}&$top={top}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var issues = new List<YouTrackIssue>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var id = element.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? string.Empty : string.Empty;
            var idReadable = element.TryGetProperty("idReadable", out var idReadableProperty) ? idReadableProperty.GetString() ?? string.Empty : string.Empty;
            var summary = element.TryGetProperty("summary", out var summaryProperty) ? summaryProperty.GetString() ?? string.Empty : string.Empty;
            var description = element.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
                ? descriptionProperty.GetString()
                : null;
            var state = _ExtractState(element);
            issues.Add(new YouTrackIssue(id, idReadable, summary, description, projectTag, state));
        }

        return issues;
    }

    /// <summary>project:{tag} #Unresolved, plus an optional extra filter (e.g. "Priority: Critical") appended verbatim.</summary>
    internal static string BuildQuery(string projectTag, string? extraFilter)
    {
        var query = $"project:{projectTag} #Unresolved";
        return string.IsNullOrWhiteSpace(extraFilter) ? query : $"{query} {extraFilter.Trim()}";
    }

    /// <summary>The issue's web URL, derived from the API base URL by dropping a trailing "/api" — e.g. "https://x.youtrack.cloud/api" -> "https://x.youtrack.cloud/issue/PROJ-123".</summary>
    internal static string BuildIssueUrl(string instanceBaseUrl, string idReadable)
    {
        var trimmed = instanceBaseUrl.TrimEnd('/');
        if (trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return $"{trimmed}/issue/{idReadable}";
    }

    // "State" (most projects) or "Stage" (e.g. EJ, per the YouTrack skill) — the first matching custom field's value name.
    private static string? _ExtractState(JsonElement element)
    {
        if (!element.TryGetProperty("customFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var field in fields.EnumerateArray())
        {
            var name = field.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            if (name is not ("State" or "Stage"))
            {
                continue;
            }

            if (field.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("name", out var valueName) && valueName.ValueKind == JsonValueKind.String)
            {
                return valueName.GetString();
            }
        }

        return null;
    }
}
