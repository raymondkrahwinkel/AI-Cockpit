using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugin.Depot.Model;
using Microsoft.Extensions.Logging;

namespace Cockpit.Plugin.Depot.Notes.Live;

// The live notes that are open, one DepotNoteSession each, keyed by connection and note. The host-only token stays behind
// handlerFor: this class, the sessions and the channel only ever see the handler.
internal sealed class DepotNoteLive(
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, HttpMessageHandler> handlerFor,
    DepotNotesHubFactory hubFactory,
    DepotNoteRoomFactory roomFactory,
    Action<DepotNoteLiveStatus> publish,
    ILogger? logger = null,
    Func<DepotConnectionRegistration, CancellationToken, Task<bool>>? signIn = null) : IAsyncDisposable
{
    private readonly Dictionary<string, DepotNoteSession> _sessions = new(StringComparer.Ordinal);
    // Notes asked for live until the plugin closes them: a tab that closes and opens again gets a new session without asking twice.
    private readonly HashSet<string> _wanted = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    // The note's session, started if it was not open yet; the host's tab disposes it, which takes it off the list again.
    public DepotNoteSession Open(DepotNoteLiveRequest request)
    {
        var connection = connections().FirstOrDefault(candidate => candidate.Id == request.ConnectionId)
            ?? throw new ArgumentException("That Depot connection is no longer configured.", nameof(request));
        DepotNoteSession session;
        lock (_lock)
        {
            var key = _Key(request);
            _wanted.Add(key);
            if (_sessions.TryGetValue(key, out var open))
            {
                session = open;
            }
            else
            {
                session = new DepotNoteSession(
                    connection, request, cancellationToken => ReadUserAsync(connection, handlerFor(connection), cancellationToken),
                    hubFactory, roomFactory, publish, logger: logger, disposed: _Forget,
                    post: (content, baseChecksum, session, cancellationToken) =>
                        PostAsync(connection, handlerFor(connection), request, content, baseChecksum, session, cancellationToken),
                    signIn: signIn is null ? null : cancellationToken => signIn(connection, cancellationToken));
                _sessions[key] = session;
            }
        }

        // A session that stopped on SignInNeeded starts again here, once the user has signed in; one that is running ignores it.
        session.Connect();
        return session;
    }

    public bool IsOpen(DepotNoteLiveRequest request)
    {
        lock (_lock)
        {
            return _wanted.Contains(_Key(request));
        }
    }

    public async Task CloseAsync(DepotNoteLiveRequest request)
    {
        DepotNoteSession? session;
        lock (_lock)
        {
            _wanted.Remove(_Key(request));
            _sessions.Remove(_Key(request), out session);
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The sign-in changed (AC-1665): each open note checks whom Depot names now, and joins or saves again only as the account it
    // opened with (AC-1582). Text typed here stays in the note's session either way.
    public void AccountChanged(string connectionId)
    {
        List<DepotNoteSession> open;
        lock (_lock)
        {
            open = [.. _sessions.Values.Where(session => session.Connection.Id == connectionId)];
        }

        foreach (var session in open)
        {
            session.AccountChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<DepotNoteSession> sessions;
        lock (_lock)
        {
            sessions = [.. _sessions.Values];
            _sessions.Clear();
            _wanted.Clear();
        }

        foreach (var session in sessions)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    // The subject the hub's gate compares expectedUser with (AC-1607); a 401 here is a sign-in the user has to redo.
    internal static async Task<DepotNoteUser> ReadUserAsync(DepotConnectionRegistration connection, HttpMessageHandler handler, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var response = await http.GetAsync($"{connection.Url.TrimEnd('/')}/cockpit/me", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var me = await response.Content.ReadFromJsonAsync<Me>(DepotChannel.Json, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(me?.UserId)
            ? throw new HttpRequestException("Depot did not identify the user.", null, HttpStatusCode.Unauthorized)
            : new DepotNoteUser(me.UserId, me.Name, me.Color);
    }

    private void _Forget(DepotNoteSession session)
    {
        lock (_lock)
        {
            var key = _Key(session.Note);
            if (_sessions.TryGetValue(key, out var open) && open == session)
            {
                _sessions.Remove(key);
            }
        }
    }

    // Protocol section 6 duty 3, the bearer door (DEP-528): URL-encoded, so a file keeps its own line endings, with no antiforgery.
    internal static async Task<DepotNoteSaveAnswer> PostAsync(
        DepotConnectionRegistration connection, HttpMessageHandler handler, DepotNoteLiveRequest note, string content, string baseChecksum, string session,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        using var form = new FormUrlEncodedContent([
            new("path", note.Path), new("baseChecksum", baseChecksum), new("content", content), new("session", session)]);
        var url = $"{connection.Url.TrimEnd('/')}/cockpit/projects/{Uri.EscapeDataString(note.Organization)}/{Uri.EscapeDataString(note.Project)}/note-save";
        using var response = await http.PostAsync(url, form, cancellationToken).ConfigureAwait(false);
        // A 403 or a 400 may carry no JSON at all; the status alone decides then.
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var answer = body.StartsWith('{') ? JsonSerializer.Deserialize<SaveResponse>(body, DepotChannel.Json) : null;
        return new DepotNoteSaveAnswer((int)response.StatusCode, answer?.Checksum, answer?.Version, answer?.Own == true);
    }

    private static string _Key(DepotNoteLiveRequest request) => $"{request.ConnectionId}|{request.Organization}/{request.Project}/{request.Path}";

    private sealed record Me(string? UserId, string? Name = null, string? Color = null);

    private sealed record SaveResponse(string? Checksum, int? Version, bool? Own);
}
