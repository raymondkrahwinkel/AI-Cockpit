using System.Diagnostics;
using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Depot.UI;

// AC-1586: the UI half of the Depot inbox. It keeps the latest page per connection as the backend reports it, so the
// side-menu badge can add them up while the tab shows one; every Depot call is the backend's, over the channel.
internal sealed class DepotInboxModel : IDisposable
{
    private readonly ICockpitUiHost _host;
    private readonly Dictionary<string, DepotInboxPage> _pages = new(StringComparer.Ordinal);
    private readonly Lock _pagesLock = new();
    private readonly IDisposable _subscription;

    public DepotInboxModel(ICockpitUiHost host)
    {
        _host = host;
        _subscription = host.Channel.Subscribe(DepotInboxChannel.Changed, _OnChanged);
    }

    // Raised on whatever thread the backend published from.
    public event Action<DepotInboxPage>? Changed;

    // Unread over every connection Depot answered for, the unreachable ones at their last count; null while none did.
    public int? Total
    {
        get
        {
            lock (_pagesLock)
            {
                var counted = _pages.Values.Select(page => page.Count).Where(count => count.State is DepotInboxState.Ok or DepotInboxState.Unreachable).ToList();
                return counted.Count == 0 ? null : counted.Sum(count => count.Unread);
            }
        }
    }

    public DepotInboxPage? PageOf(string connectionId)
    {
        lock (_pagesLock)
        {
            return _pages.GetValueOrDefault(connectionId);
        }
    }

    public async Task LoadCountsAsync()
    {
        try
        {
            var answer = await _host.Channel.InvokeAsync(DepotInboxChannel.Unread, JsonSerializer.SerializeToElement(new { }, DepotChannel.Json)).ConfigureAwait(true);
            foreach (var count in answer.Deserialize<IReadOnlyList<DepotInboxCount>>(DepotChannel.Json) ?? [])
            {
                _Apply(PageOf(count.ConnectionId) is { } known ? known with { Count = count } : new DepotInboxPage(count, [], null));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A badge left unknown until the first push is the honest fallback.
            Trace.TraceWarning($"Loading Depot inbox counts failed: {exception}");
        }
    }

    public Task<DepotInboxPage?> ListAsync(string connectionId) =>
        _InvokeAsync(DepotInboxChannel.List, new DepotInboxRequest(connectionId));

    public Task<DepotInboxPage?> MarkReadAsync(string connectionId, string messageId) =>
        _InvokeAsync(DepotInboxChannel.MarkRead, new DepotInboxRequest(connectionId, messageId));

    // Only the operator's click gets here; nothing else opens a browser (AC-1517).
    public async Task<bool> SignInAsync(DepotConnectionRegistration connection) =>
        await _host.SignInMcpServerAsync(connection.McpServerName).ConfigureAwait(true) == PluginMcpSignInOutcome.Authorized;

    // The one action a message has in Cockpit: no editor yet (AC-1580), so a note invitation opens its note in Depot and
    // every other kind its message there, where accepting, granting and deciding happen.
    public static (string Label, string Url) PrimaryAction(DepotInboxMessage message) =>
        message.Kind == DepotInboxKind.NoteInvitation
            ? ("Open in Depot ↗", message.Note?.OpenUrl ?? message.OpenUrl)
            : ("Decide in Depot ↗", message.OpenUrl);

    public static Task OpenAsync(DepotInboxMessage message, Func<Uri, Task> launch) =>
        Uri.TryCreate(PrimaryAction(message).Url, UriKind.Absolute, out var uri) ? launch(uri) : Task.CompletedTask;

    // AC-1581 replaces this browser fallback with the native note join when the editor route is available.
    public static Task JoinAsync(DepotInboxMessage message, Func<Uri, Task> launch) => OpenAsync(message, launch);

    public void Dispose() => _subscription.Dispose();

    private void _OnChanged(PluginChannelEvent channelEvent)
    {
        if (channelEvent.Payload.Deserialize<DepotInboxPage>(DepotChannel.Json) is { } page)
        {
            _Apply(page);
        }
    }

    private void _Apply(DepotInboxPage page)
    {
        lock (_pagesLock)
        {
            _pages[page.Count.ConnectionId] = page;
        }

        Changed?.Invoke(page);
    }

    private async Task<DepotInboxPage?> _InvokeAsync(string action, DepotInboxRequest request)
    {
        try
        {
            var answer = await _host.Channel.InvokeAsync(action, JsonSerializer.SerializeToElement(request, DepotChannel.Json)).ConfigureAwait(true);
            if (answer.Deserialize<DepotInboxPage>(DepotChannel.Json) is not { } page)
            {
                return null;
            }

            _Apply(page);
            return page;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }
}
