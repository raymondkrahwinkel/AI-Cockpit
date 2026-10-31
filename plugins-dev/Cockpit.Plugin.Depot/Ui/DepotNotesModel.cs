using System.Diagnostics;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

internal sealed class DepotNotesModel : IDisposable
{
    private readonly ICockpitUiHost _host;
    private readonly DepotInboxModel _inbox;
    private readonly Dictionary<string, DepotNotesPage> _pages = new(StringComparer.Ordinal);
    private readonly Lock _pagesLock = new();
    private readonly IDisposable _subscription;

    public DepotNotesModel(ICockpitUiHost host, DepotInboxModel inbox)
    {
        _host = host;
        _inbox = inbox;
        _subscription = host.Channel.Subscribe(DepotNotesChannel.Changed, _OnChanged);
        inbox.Changed += _OnInboxChanged;
    }

    public event Action<DepotNotesPage>? Changed;

    // AC-1579/AC-1581 replace the browser fallbacks on this shared model; the plugin sets both to its native routes.
    public Func<DepotConnectionRegistration, string?, Task>? NewMeetingNote { get; set; }
    public Func<DepotConnectionRegistration, DepotLiveNote, Task>? JoinNote { get; set; }

    public int Total
    {
        get
        {
            lock (_pagesLock)
            {
                return _pages.Values.Sum(page => page.Notes.Count);
            }
        }
    }

    public DepotNotesPage? PageOf(string connectionId)
    {
        lock (_pagesLock)
        {
            return _pages.GetValueOrDefault(connectionId);
        }
    }

    public async Task LoadAsync(IReadOnlyList<DepotConnectionRegistration> connections)
    {
        foreach (var connection in connections)
        {
            await ListAsync(connection.Id).ConfigureAwait(true);
        }
    }

    public async Task<DepotNotesPage?> ListAsync(string connectionId)
    {
        try
        {
            var answer = await _host.Channel.InvokeAsync(DepotNotesChannel.Live,
                JsonSerializer.SerializeToElement(new DepotNotesRequest(connectionId), DepotChannel.Json)).ConfigureAwait(true);
            if (answer.Deserialize<DepotNotesPage>(DepotChannel.Json) is not { } page)
            {
                return null;
            }

            _Apply(page);
            return page;
        }
        catch (Exception exception)
        {
            Trace.TraceWarning($"Loading Depot live notes failed: {exception}");
            _Apply(new(connectionId, DepotInboxState.Unreachable, []));
            return null;
        }
    }

    public int InboxCount(string connectionId) => _inbox.PageOf(connectionId)?.Count.Unread ?? 0;

    public string? InviterOf(string connectionId, DepotLiveNote note)
    {
        var token = _Token(note.Href);
        if (token is null)
        {
            return null;
        }

        var invitation = _inbox.PageOf(connectionId)?.Messages
            .Where(message => message.Kind == DepotInboxKind.NoteInvitation && message.Note?.State == "note"
                && _Token(message.Note.OpenUrl) == token)
            .OrderByDescending(message => message.CreatedAt).FirstOrDefault();
        if (invitation?.Inviter is { Length: > 0 } inviter)
        {
            return inviter;
        }

        // DEP-535 adds structured names. Until then only Depot's exact English summary format is recognized.
        const string suffix = " invites you to a note";
        return invitation is { Summary: { } summary } && summary.EndsWith(suffix, StringComparison.Ordinal)
            && summary.Length > suffix.Length ? summary[..^suffix.Length] : null;
    }

    public Task JoinAsync(DepotConnectionRegistration connection, DepotLiveNote note, Func<Uri, Task> launch) =>
        JoinNote is { } join ? join(connection, note) : _Launch(connection, note.Href, launch);

    public Task NewAsync(DepotConnectionRegistration connection, string? project, Func<Uri, Task> launch) =>
        NewMeetingNote is { } create ? create(connection, project) : _Launch(connection,
            project is null ? "/" : "/projects/" + string.Join('/', project.Split('/').Select(Uri.EscapeDataString)), launch);

    public void Dispose()
    {
        _subscription.Dispose();
        _inbox.Changed -= _OnInboxChanged;
    }

    private void _OnChanged(PluginChannelEvent evt)
    {
        if (evt.Payload.Deserialize<DepotNotesPage>(DepotChannel.Json) is { } page)
        {
            _Apply(page);
        }
    }

    private void _OnInboxChanged(DepotInboxPage inboxPage)
    {
        var id = inboxPage.Count.ConnectionId;
        if (PageOf(id) is { } page)
        {
            _Apply(inboxPage.Count.State is DepotInboxState.SignIn or DepotInboxState.Expired
                ? new(id, inboxPage.Count.State, []) : page);
        }
    }

    private void _Apply(DepotNotesPage page)
    {
        if (page.State is DepotInboxState.SignIn or DepotInboxState.Expired)
        {
            page = page with { Notes = [] };
        }

        lock (_pagesLock)
        {
            _pages[page.ConnectionId] = page;
        }

        Changed?.Invoke(page);
    }

    private static string? _Token(string? href)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        return path?.StartsWith("/n/", StringComparison.Ordinal) == true && path.Length > 3 ? path[3..] : null;
    }

    private static Task _Launch(DepotConnectionRegistration connection, string href, Func<Uri, Task> launch)
    {
        var origin = new Uri(connection.Url.TrimEnd('/') + "/");
        return Uri.TryCreate(origin, href, out var uri) && uri.Scheme == origin.Scheme && uri.Authority == origin.Authority
            ? launch(uri) : Task.CompletedTask;
    }
}
