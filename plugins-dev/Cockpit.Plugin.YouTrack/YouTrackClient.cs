using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// Fetches open ("#Unresolved") issues for a single YouTrack instance over a plain <see cref="HttpClient"/>,
/// authenticated with a permanent token — YouTrack has no local CLI equivalent to <c>gh</c>, so this plugin
/// is HTTP-only. Mirrors the query shape from the YouTrack skill: <c>GET {instance}/issues?fields=…&amp;
/// query=[project:{tag}] #Unresolved [extra]&amp;$top={n}</c>, where a null/empty project tag means every
/// project on the instance (the dialog's "All" filter, #48) — the response's own <c>project.shortName</c>
/// tells each issue which project it belongs to either way. Callers are expected to validate that the
/// instance URL and token are set before calling — this client assumes valid input and lets HTTP/JSON
/// failures surface as exceptions for the UI layer to report.
/// </summary>
internal sealed class YouTrackClient
{
    private static readonly HttpClient Http = new();

    public async Task<IReadOnlyList<YouTrackIssue>> GetOpenIssuesAsync(string instanceBaseUrl, string token, string? projectTag, string? extraFilter, int top, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var query = BuildQuery(projectTag, extraFilter);
        var url = $"{baseUrl}/issues?fields=idReadable,id,summary,description,project(shortName),customFields(name,value(name))&query={Uri.EscapeDataString(query)}&$top={top}";

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
            var project = _ExtractProject(element, projectTag);
            var state = _ExtractState(element);
            issues.Add(new YouTrackIssue(id, idReadable, summary, description, project, state));
        }

        return issues;
    }

    /// <summary>
    /// Projects configured on the instance (short-name + full name) via the admin API (needs the token's
    /// account to have project-admin read access). Returns an empty list — never throws — when that call fails,
    /// e.g. a token scoped without admin access; the dialog then falls back to the projects already present in
    /// the fetched issues (#48).
    /// </summary>
    public async Task<IReadOnlyList<YouTrackProject>> GetProjectsAsync(string instanceBaseUrl, string token, CancellationToken cancellationToken)
    {
        try
        {
            var baseUrl = instanceBaseUrl.TrimEnd('/');
            var url = $"{baseUrl}/admin/projects?fields=shortName,name";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await Http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

            var projects = new List<YouTrackProject>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.TryGetProperty("shortName", out var shortNameProperty) && shortNameProperty.ValueKind == JsonValueKind.String
                    && shortNameProperty.GetString() is { Length: > 0 } shortName)
                {
                    var name = element.TryGetProperty("name", out var nameProperty) && nameProperty.ValueKind == JsonValueKind.String
                        ? nameProperty.GetString() ?? string.Empty
                        : string.Empty;
                    projects.Add(new YouTrackProject(shortName, name));
                }
            }

            return projects;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>[project:{tag}] #Unresolved, plus an optional extra filter (e.g. "Priority: Critical") appended verbatim. A null/empty tag omits the project clause, matching every project on the instance.</summary>
    internal static string BuildQuery(string? projectTag, string? extraFilter)
    {
        var query = string.IsNullOrWhiteSpace(projectTag) ? "#Unresolved" : $"project:{projectTag} #Unresolved";
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

    // The response's own project.shortName when present, otherwise the tag the query was scoped to (absent
    // for an "All projects" query, in which case the issue simply shows no project).
    private static string _ExtractProject(JsonElement element, string? fallbackProjectTag)
    {
        if (element.TryGetProperty("project", out var project) && project.ValueKind == JsonValueKind.Object
            && project.TryGetProperty("shortName", out var shortNameProperty) && shortNameProperty.ValueKind == JsonValueKind.String
            && shortNameProperty.GetString() is { Length: > 0 } shortName)
        {
            return shortName;
        }

        return fallbackProjectTag ?? string.Empty;
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
