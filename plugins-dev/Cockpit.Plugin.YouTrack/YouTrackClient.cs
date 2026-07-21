using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Tracking;

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

    public async Task<IReadOnlyList<YouTrackIssue>> GetOpenIssuesAsync(string instanceBaseUrl, string token, string? projectTag, string? extraFilter, bool assignedToMe, int top, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var query = BuildQuery(projectTag, extraFilter, assignedToMe);
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
    /// One issue, by the id a human writes ("EVE-14") — what a workflow step is given, since a flow refers to a
    /// ticket the way you would say it out loud, not by the internal id nobody sees.
    /// </summary>
    public async Task<YouTrackIssue> GetIssueAsync(string instanceBaseUrl, string token, string idReadable, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var json = await _GetAsync(
            $"{baseUrl}/issues/{Uri.EscapeDataString(idReadable)}?fields=idReadable,id,summary,description,project(shortName),customFields(name,value(name))",
            token,
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        var element = document.RootElement;

        var id = element.TryGetProperty("id", out var idProperty) ? idProperty.GetString() ?? string.Empty : string.Empty;
        if (id.Length == 0)
        {
            throw new InvalidOperationException($"YouTrack does not know an issue called '{idReadable}'.");
        }

        var readable = element.TryGetProperty("idReadable", out var readableProperty) ? readableProperty.GetString() ?? idReadable : idReadable;
        var summary = element.TryGetProperty("summary", out var summaryProperty) ? summaryProperty.GetString() ?? string.Empty : string.Empty;
        var description = element.TryGetProperty("description", out var descriptionProperty) && descriptionProperty.ValueKind == JsonValueKind.String
            ? descriptionProperty.GetString()
            : null;

        return new YouTrackIssue(id, readable, summary, description, _ExtractProject(element, null), _ExtractState(element));
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

    /// <summary>
    /// The status field of one issue as its project defines it (#75) — which field is the status, what it is
    /// worth now, and what it may become. Reads the issue's own custom fields (no admin rights needed), then
    /// fills in the possible moves: a workflow-governed field is asked for its <c>possibleEvents</c>, an
    /// ordinary one for the project's allowed values, falling back to the admin route when the issue response
    /// did not carry the bundle. When neither yields anything the field comes back with no targets, and the UI
    /// offers no status actions rather than actions that would be refused.
    /// </summary>
    public async Task<YouTrackIssueFields> GetIssueFieldsAsync(string instanceBaseUrl, string token, YouTrackIssue issue, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var json = await _GetAsync(
            $"{baseUrl}/issues/{issue.IdReadable}/customFields?fields=id,name,$type,value(name),projectCustomField(field(name),bundle(values(name)))",
            token,
            cancellationToken);

        var fields = YouTrackFieldParser.Parse(json);
        if (fields.State is not { } state)
        {
            return fields;
        }

        if (state.IsStateMachine)
        {
            var eventsJson = await _GetAsync(
                $"{baseUrl}/issues/{issue.IdReadable}/customFields/{state.Id}?fields=$type,possibleEvents(id,presentation)",
                token,
                cancellationToken);

            return fields with { State = state with { PossibleEvents = YouTrackFieldParser.ParsePossibleEvents(eventsJson) } };
        }

        if (state.Values.Count > 0)
        {
            return fields;
        }

        var values = await _GetProjectFieldValuesAsync(baseUrl, token, issue.Project, state.Name, cancellationToken);
        return fields with { State = state with { Values = values } };
    }

    /// <summary>Moves an issue's status to <paramref name="target"/> — a value on an ordinary field, an event on a workflow-governed one. Throws when YouTrack refuses (an undefined transition, or no permission), so the caller can say why.</summary>
    public Task SetStateAsync(string instanceBaseUrl, string token, YouTrackIssue issue, YouTrackStateField field, string target, CancellationToken cancellationToken) =>
        _PostIssueAsync(instanceBaseUrl, token, issue, YouTrackUpdateBody.ForState(field, target), cancellationToken);

    /// <summary>Assigns the issue to the token's own account (<c>GET /users/me</c>), on the project's assignee field.</summary>
    public async Task AssignToMeAsync(string instanceBaseUrl, string token, YouTrackIssue issue, string assigneeFieldName, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        var meJson = await _GetAsync($"{baseUrl}/users/me?fields=login", token, cancellationToken);

        using var document = JsonDocument.Parse(meJson);
        if (!document.RootElement.TryGetProperty("login", out var login) || login.GetString() is not { Length: > 0 } value)
        {
            throw new InvalidOperationException("YouTrack did not say who the token belongs to, so the issue was not assigned.");
        }

        await _PostIssueAsync(baseUrl, token, issue, YouTrackUpdateBody.ForAssignee(assigneeFieldName, value), cancellationToken);
    }

    // The project's allowed values for one field. Needs the token's account to read the project's field
    // configuration; returns empty — never throws — when it may not, same fail-open shape as GetProjectsAsync.
    private async Task<IReadOnlyList<string>> _GetProjectFieldValuesAsync(string baseUrl, string token, string projectShortName, string fieldName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectShortName))
        {
            return [];
        }

        try
        {
            var json = await _GetAsync(
                $"{baseUrl}/admin/projects/{projectShortName}/customFields?fields=field(name),bundle(values(name))",
                token,
                cancellationToken);

            return YouTrackFieldParser.ParseProjectFieldValues(json, fieldName);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Attaches a file to an issue (AC-14): a multipart POST to <c>{instance}/issues/{id}/attachments</c>, the way
    /// the YouTrack REST API takes an upload. Throws with YouTrack's own reason when it refuses (no permission, too
    /// large), so the caller can say why.
    /// </summary>
    public async Task AttachFileAsync(string instanceBaseUrl, string token, string idReadable, string fileName, byte[] bytes, string mediaType, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        // A unique form-field name per file is what YouTrack expects for a multipart attachment upload.
        content.Add(file, fileName, fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/issues/{Uri.EscapeDataString(idReadable)}/attachments?fields=id,name");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var failure = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"YouTrack refused the attachment ({(int)response.StatusCode}): {YouTrackErrorMessage.From(failure)}");
    }

    private static async Task<string> _GetAsync(string url, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    // YouTrack answers a refused update with a body that says why (an undefined transition, a value the field
    // does not have, no permission) — surface it, because "it did not work" is not something the operator can act on.
    private static async Task _PostIssueAsync(string instanceBaseUrl, string token, YouTrackIssue issue, string body, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/issues/{issue.IdReadable}?fields=idReadable");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var failure = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"YouTrack refused the update ({(int)response.StatusCode}): {YouTrackErrorMessage.From(failure)}");
    }

    /// <summary>Posts a comment on an issue (<c>POST /issues/{id}/comments</c>). Surfaces YouTrack's own refusal text on failure.</summary>
    public async Task AddCommentAsync(string instanceBaseUrl, string token, string idReadable, string text, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/issues/{Uri.EscapeDataString(idReadable)}/comments?fields=id");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(JsonSerializer.Serialize(new { text }), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var failure = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"YouTrack refused the comment ({(int)response.StatusCode}): {YouTrackErrorMessage.From(failure)}");
    }

    /// <summary>Reads an issue's comments (<c>GET /issues/{id}/comments</c>), normalized to <see cref="TrackerComment"/> (YouTrack's <c>created</c> is epoch-ms).</summary>
    public async Task<IReadOnlyList<TrackerComment>> ReadCommentsAsync(string instanceBaseUrl, string token, string idReadable, CancellationToken cancellationToken)
    {
        var baseUrl = instanceBaseUrl.TrimEnd('/');
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/issues/{Uri.EscapeDataString(idReadable)}/comments?fields=id,text,created,author(login)");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var comments = new List<TrackerComment>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var text = element.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String ? textValue.GetString() ?? string.Empty : string.Empty;
            var created = element.TryGetProperty("created", out var createdValue) && createdValue.ValueKind == JsonValueKind.Number ? createdValue.GetInt64() : 0L;
            var login = element.TryGetProperty("author", out var author) && author.ValueKind == JsonValueKind.Object && author.TryGetProperty("login", out var loginValue)
                ? loginValue.GetString() ?? string.Empty
                : string.Empty;
            comments.Add(new TrackerComment(login, text, DateTimeOffset.FromUnixTimeMilliseconds(created)));
        }

        return comments;
    }

    /// <summary>
    /// [project:{tag}] plus what to look for — <c>#Unresolved</c> unless the caller says otherwise, because showing
    /// issues that are done is offering work that is over — plus <c>for: me</c> when <paramref name="assignedToMe"/>
    /// (YouTrack's own "assigned to the current user" clause, resolved against the token). A null/empty tag omits the
    /// project clause, matching every project on the instance.
    /// <para>
    /// <paramref name="filter"/> replaces <c>#Unresolved</c> rather than being appended to it: an operator who writes
    /// "State: Done" means it, and a query that quietly kept "#Unresolved" in front of it would return nothing and
    /// look like a broken search.
    /// </para>
    /// </summary>
    internal static string BuildQuery(string? projectTag, string? filter, bool assignedToMe)
    {
        var what = string.IsNullOrWhiteSpace(filter) ? "#Unresolved" : filter.Trim();

        var query = string.IsNullOrWhiteSpace(projectTag) ? what : $"project:{projectTag} {what}";

        return assignedToMe ? $"{query} for: me" : query;
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
