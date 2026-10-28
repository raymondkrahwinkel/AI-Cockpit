using System.Globalization;
using System.Text.Json;
using Cockpit.Plugin.Depot.Browser;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Depot.Notes;

// AC-1579: the meeting-note calls to Depot's /cockpit routes (DEP-448) through the host's authorized handler. The
// backend sends the operator's choices; Depot renders the Markdown from its template and sends the invitations.
internal sealed class DepotMeetingNoteService(
    ICockpitHost host, Func<IReadOnlyList<DepotConnectionRegistration>> connections, Func<DateOnly>? today = null)
{
    private readonly Func<DateOnly> _today = today ?? (() => DateOnly.FromDateTime(DateTime.Now));

    public Task<DepotBrowserAnswer<DepotMeetingNoteOptions>> OptionsAsync(DepotMeetingNoteRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            using var document = await _GetAsync(request.ConnectionId, "cockpit/meeting-note/options", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return new DepotMeetingNoteOptions(
                [.. _Items(root, "projects").Select(project => new DepotMeetingNoteProject(
                    DepotRequests.String(project, "address") ?? string.Empty,
                    DepotRequests.String(project, "name") ?? string.Empty,
                    DepotRequests.String(project, "organization") ?? string.Empty,
                    DepotRequests.String(project, "role") ?? string.Empty,
                    DepotRequests.String(project, "folder"),
                    project.TryGetProperty("allowed", out var allowed) && allowed.ValueKind == JsonValueKind.True,
                    DepotRequests.String(project, "planHref"))).Where(project => project.Address.Length > 0)],
                root.TryGetProperty("leftOut", out var leftOut) && leftOut.TryGetInt32(out var count) ? count : 0,
                [.. _Items(root, "recent").Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0)]);
        });

    // Folders and attendees in one answer: the dialog needs both the moment a project is chosen.
    public Task<DepotBrowserAnswer<DepotMeetingNoteProjectInfo>> ProjectAsync(DepotMeetingNoteRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var root = _Root(request);
            using var folders = await _GetAsync(request.ConnectionId, $"{root}/folders", cancellationToken).ConfigureAwait(false);
            using var people = await _GetAsync(request.ConnectionId, $"{root}/people", cancellationToken).ConfigureAwait(false);
            return new DepotMeetingNoteProjectInfo(
                [.. _Items(folders.RootElement, "folders").Select(item => item.GetString() ?? string.Empty).Where(item => item.Length > 0)],
                [.. _Items(people.RootElement, "people").Select(person => new DepotMeetingNotePerson(
                    DepotRequests.String(person, "id") ?? string.Empty,
                    DepotRequests.String(person, "name") ?? string.Empty,
                    DepotRequests.String(person, "role"))).Where(person => person.Id.Length > 0)]);
        });

    public Task<DepotBrowserAnswer<DepotMeetingNoteCheck>> CheckAsync(DepotMeetingNoteRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var path = _NotePath(request);
            using var document = await _GetAsync(
                request.ConnectionId, $"{_Root(request)}/check?path={Uri.EscapeDataString(path)}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            return new DepotMeetingNoteCheck(
                root.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.True,
                DepotRequests.String(root, "by"),
                root.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.String && at.TryGetDateTimeOffset(out var when) ? when : null,
                root.TryGetProperty("live", out var live) && live.TryGetInt32(out var count) ? count : 0);
        });

    public Task<DepotBrowserAnswer<DepotMeetingNoteCreated>> CreateAsync(DepotMeetingNoteRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var topic = DepotRequests.Required(request.Topic, "topic");
            var template = request.Template is DepotMeetingNoteTemplate.Meeting or DepotMeetingNoteTemplate.Empty
                ? request.Template
                : throw new DepotFailure(DepotBrowserStatus.Error, "Choose a template.");
            var date = _Date(request);
            var path = DepotMeetingNoteName.Build(request.Folder, topic, date);

            // The whole of what Cockpit sends: where, what it is about, which template, who. No content.
            List<KeyValuePair<string, string>> form =
            [
                new("path", path),
                new("topic", topic.Trim()),
                new("template", template),
                new("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                .. (request.Attendees ?? []).Select(id => new KeyValuePair<string, string>("attendee", id)),
            ];
            using var document = await _PostFormAsync(
                request.ConnectionId, _Root(request), form, cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var message = DepotRequests.String(root, "message") ?? path;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return new DepotMeetingNoteCreated(message, Existing: false);
            }

            return DepotRequests.String(root, "code") == "path-in-use"
                ? new DepotMeetingNoteCreated(DepotRequests.String(root, "existing") ?? path, Existing: true)
                : throw new DepotFailure(DepotBrowserStatus.Error, message);
        });

    // The note's link in Depot's own web viewer, for the browser until the note opens in Cockpit's editor.
    public Task<DepotBrowserAnswer<string>> LinkAsync(DepotMeetingNoteRequest request, CancellationToken cancellationToken) =>
        DepotRequests.RunAsync(async () =>
        {
            var path = DepotRequests.Required(request.Path, "path");
            using var document = await _PostFormAsync(
                request.ConnectionId, _Root(request, "note-link"), [new("path", path)], cancellationToken).ConfigureAwait(false);
            var connection = _Connection(request.ConnectionId);
            return DepotRequests.String(document.RootElement, "url") is { Length: > 1 } url && url[0] == '/' && url[1] != '/' && url[1] != '\\'
                ? connection.Url.TrimEnd('/') + url
                : throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not return a link to the note.");
        });

    private static string _Root(DepotMeetingNoteRequest request, string leaf = "meeting-note")
    {
        var address = DepotRequests.Required(request.Project, "project").Split('/', 2);
        return address.Length == 2 && address.All(part => !string.IsNullOrWhiteSpace(part))
            ? $"cockpit/projects/{Uri.EscapeDataString(address[0])}/{Uri.EscapeDataString(address[1])}/{leaf}"
            : throw new DepotFailure(DepotBrowserStatus.Error, "The Depot project has no organization-qualified address.");
    }

    private DateOnly _Date(DepotMeetingNoteRequest request) =>
        DateOnly.TryParseExact(request.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : _today();

    private string _NotePath(DepotMeetingNoteRequest request) =>
        DepotMeetingNoteName.Build(request.Folder, DepotRequests.Required(request.Topic, "topic"), _Date(request));

    private static IEnumerable<JsonElement> _Items(JsonElement root, string name) =>
        root.TryGetProperty(name, out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private DepotConnectionRegistration _Connection(string connectionId) =>
        connections().FirstOrDefault(candidate => candidate.Id == connectionId)
        ?? throw new DepotFailure(DepotBrowserStatus.Error, "That Depot connection is no longer configured.");

    private async Task<JsonDocument> _GetAsync(string connectionId, string path, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        using var api = new DepotApi(host, connection);
        return await _DocumentAsync(api.GetAsync(path, cancellationToken), connection.Name, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> _PostFormAsync(
        string connectionId, string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
    {
        var connection = _Connection(connectionId);
        using var api = new DepotApi(host, connection);
        return await _DocumentAsync(api.PostFormAsync(path, form, cancellationToken), connection.Name, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> _DocumentAsync(Task<DepotApiResult> call, string connectionName, CancellationToken cancellationToken)
    {
        DepotApiResult result;
        try
        {
            result = await call.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new DepotFailure(DepotBrowserStatus.Error, "Could not reach Depot, or it took too long.");
        }

        switch (result.Outcome)
        {
            case DepotApiOutcome.NotSignedIn:
                throw new DepotFailure(DepotBrowserStatus.SignIn, $"Sign in to {connectionName}");
            case DepotApiOutcome.Forbidden:
                throw new DepotFailure(DepotBrowserStatus.Error, "Depot refused: you need the Editor role on this project, and a plan with the notes-taker.");
            case DepotApiOutcome.OlderDepot:
                throw new DepotFailure(DepotBrowserStatus.Error, "This Depot is too old for meeting notes from Cockpit.");
            case not DepotApiOutcome.Success:
                throw new DepotFailure(DepotBrowserStatus.Error, "Depot did not accept the request.");
        }

        return DepotRequests.Parse(System.Text.Encoding.UTF8.GetString(result.Content.Span));
    }
}
