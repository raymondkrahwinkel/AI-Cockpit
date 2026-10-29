using System.Diagnostics;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// What an entry knows about the note it opens: Depot's opaque token, and the inbox message it came from, if any.
internal sealed record DepotNoteRef(string Token, string? InboxMessageId = null, string? Title = null)
{
    // A /n/{token} link, relative or absolute; null for anything else.
    public static DepotNoteRef? FromHref(string? href, string? inboxMessageId = null)
    {
        var path = Uri.TryCreate(href, UriKind.Absolute, out var uri) ? uri.AbsolutePath : href;
        return path?.StartsWith("/n/", StringComparison.Ordinal) == true && DepotNoteToken.IsValid(path[3..])
            ? new DepotNoteRef(path[3..], inboxMessageId) : null;
    }
}

internal enum DepotNoteStand
{
    Edit,
    ViewOnly,
}

internal sealed record DepotNoteTarget(string Organization, string Project, string Path, string Title, DepotNoteStand Stand);

internal enum DepotNoteJoinOutcome
{
    // The note is a live tab (Edit or ViewOnly, as Depot's role says).
    Opened,

    // Depot has no note for this account: the "Shared note" tab, with neither title nor path.
    Unavailable,

    // No live tab to open yet: the note opened in the system browser, where Depot decides.
    Browser,
    SignInDeclined,
    Unreachable,
}

internal sealed record DepotNoteJoinResult(DepotNoteJoinOutcome Outcome, Func<Task>? ShowTab = null);

// AC-1581: the one route into a note for every entry (the Notes tab, the inbox, the toast, a transcript link). Depot says
// what the account may do; Cockpit shows that and nothing else, and opens nothing before the operator has asked.
internal sealed class DepotNoteRoute(
    ICockpitUiHost host,
    Func<IReadOnlyList<DepotConnectionRegistration>> connections,
    Func<DepotConnectionRegistration, string, Task<DepotNoteLinkAnswer>> resolve,
    Func<DepotConnectionRegistration, Task<bool>> signIn,
    Func<DepotConnectionRegistration, string, Func<Task>?, Task> joined,
    Func<Uri, Task> launch)
{
    // The plugin sets this to open the live tab (AC-1580); it returns what brings that tab forward again.
    public Func<DepotConnectionRegistration, DepotNoteTarget, Task<Func<Task>>>? OpenLive { get; set; }

    public static DepotNoteStand StandOf(string? role) => role == DepotNoteRole.Editor ? DepotNoteStand.Edit : DepotNoteStand.ViewOnly;

    public Task<DepotNoteJoinResult> JoinInvitationAsync(DepotConnectionRegistration connection, DepotInboxMessage message) =>
        DepotNoteRef.FromHref(message.Note?.OpenUrl, message.Id) is { } note ? JoinAsync(connection, note with { Title = message.Note?.Title }) : _NoNote();

    public Task<DepotNoteJoinResult> JoinLiveAsync(DepotConnectionRegistration connection, DepotLiveNote live) =>
        DepotNoteRef.FromHref(live.Href) is { } note ? JoinAsync(connection, note) : _NoNote();

    // A transcript link names its Depot by address; a link to a Depot that is not connected is not ours.
    public Task<DepotNoteJoinResult> JoinLinkAsync(string link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var uri) && DepotNoteRef.FromHref(uri.AbsolutePath) is { } note
            && connections().FirstOrDefault(connection => Uri.TryCreate(connection.Url, UriKind.Absolute, out var configured)
                && string.Equals(configured.Host, uri.Host, StringComparison.OrdinalIgnoreCase)) is { } match
            ? JoinAsync(match, note) : _NoNote();

    public async Task<DepotNoteJoinResult> JoinAsync(DepotConnectionRegistration connection, DepotNoteRef note)
    {
        var answer = await resolve(connection, note.Token).ConfigureAwait(true);
        if (answer.State == DepotNoteLinkState.SignIn)
        {
            if (!await signIn(connection).ConfigureAwait(true))
            {
                return new(DepotNoteJoinOutcome.SignInDeclined);
            }

            answer = await resolve(connection, note.Token).ConfigureAwait(true);
        }

        switch (answer.State)
        {
            case DepotNoteLinkState.Ok when answer.Organization is { } organization && answer.Project is { } project && answer.Path is { } path:
                return await _OpenAsync(connection, note, new(organization, project, path, answer.Title ?? System.IO.Path.GetFileName(path), StandOf(answer.Role))).ConfigureAwait(true);
            case DepotNoteLinkState.Unavailable:
                return new(DepotNoteJoinOutcome.Unavailable, await DepotSharedNote.OpenAsync(host, connection, note.Token, note.Title).ConfigureAwait(true));
            default:
                host.ShowToast($"{connection.Name} did not answer. Try again in a moment.", PluginToastSeverity.Warning);
                return new(DepotNoteJoinOutcome.Unreachable);
        }
    }

    private async Task<DepotNoteJoinResult> _OpenAsync(DepotConnectionRegistration connection, DepotNoteRef note, DepotNoteTarget target)
    {
        if (OpenLive is not { } open)
        {
            await _BrowserAsync(connection, note.Token).ConfigureAwait(true);
            return new(DepotNoteJoinOutcome.Browser);
        }

        var show = await open(connection, target).ConfigureAwait(true);
        if (note.InboxMessageId is { } messageId)
        {
            await joined(connection, messageId, show).ConfigureAwait(true);
        }

        return new(DepotNoteJoinOutcome.Opened, show);
    }

    private Task _BrowserAsync(DepotConnectionRegistration connection, string token) =>
        Uri.TryCreate($"{connection.Url.TrimEnd('/')}/n/{token}", UriKind.Absolute, out var uri) ? launch(uri) : Task.CompletedTask;

    private static Task<DepotNoteJoinResult> _NoNote() => Task.FromResult(new DepotNoteJoinResult(DepotNoteJoinOutcome.Unreachable));

    public static async Task<DepotNoteLinkAnswer> ResolveOverChannelAsync(ICockpitUiHost host, DepotConnectionRegistration connection, string token)
    {
        try
        {
            var answer = await host.Channel.InvokeAsync(DepotNoteLinkChannel.Resolve,
                JsonSerializer.SerializeToElement(new DepotNoteLinkRequest(connection.Id, token), DepotChannel.Json)).ConfigureAwait(true);
            return answer.Deserialize<DepotNoteLinkAnswer>(DepotChannel.Json) ?? new(DepotNoteLinkState.Unreachable);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Trace.TraceWarning($"Depot: resolving a note link failed: {exception.Message}");
            return new(DepotNoteLinkState.Unreachable);
        }
    }
}
