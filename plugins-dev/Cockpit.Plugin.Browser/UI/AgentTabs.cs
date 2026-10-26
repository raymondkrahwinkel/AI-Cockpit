using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Browser.UI;

// One tab of an agent browser (AC-1713): its title and address, and the session that drives it.
internal sealed record AgentTab(string PaneId, string Session, string TargetId, string Title, string Url, bool OperatorDrives, string FaviconUrl = "", Bitmap? Icon = null)
{
    // The session's name is "AC-1720 verify login"; the badge on the tab is its first word.
    public string Badge => Session.Split(' ', 2)[0];
}

// The Agents side of the profile switch: the tabs of every live agent browser, the one on screen and its picture.
// The backend half owns the browsers; this reads them over the plugin channel. The screencast runs only while the
// view is showing (SetViewing), so a closed panel costs the agent browser nothing.
internal sealed class AgentTabsModel : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IPluginUiChannel? _channel;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly DispatcherTimer _poll = new() { Interval = PollInterval };
    private List<AgentTab> _tabs = [];
    private AgentTab? _active;
    private bool _viewing;
    private string? _watching;
    private int _frameDecoding;

    public AgentTabsModel(IPluginUiChannel? channel)
    {
        _channel = channel;
        if (channel is null)
        {
            return;
        }

        _subscriptions.Add(channel.Subscribe("agents.changed", changed => Dispatcher.UIThread.Post(() => { _ = RefreshAsync(); })));
        _subscriptions.Add(channel.Subscribe("agents.frame", OnFrame));
        _poll.Tick += (_, _) => _ = RefreshAsync();
        _ = RefreshAsync();
    }

    // The count in the rail tab's badge: every tab of every agent browser.
    public DockPanelBadge Badge { get; } = new();

    public IReadOnlyList<AgentTab> Tabs => _tabs;

    public AgentTab? Active => _active;

    public Bitmap? Frame { get; private set; }

    // The page's size in CSS pixels, as the last frame reported it: what pointer positions are mapped onto.
    public (double Width, double Height) PageSize { get; private set; }

    public string? Error { get; private set; }

    // Whether the shell shows the Agents side, for the status bar line.
    public bool Showing { get; private set; }

    public event Action? Changed;

    public event Action? FrameChanged;

    public void Show(bool showing)
    {
        if (Showing != showing)
        {
            Showing = showing;
            Changed?.Invoke();
        }
    }

    public void SetViewing(bool viewing)
    {
        _viewing = viewing;
        _poll.IsEnabled = viewing && _channel is not null;
        _ = SyncWatchAsync();
    }

    public void Activate(AgentTab tab)
    {
        _active = tab;
        Frame = null;
        Changed?.Invoke();
        _ = SyncWatchAsync();
    }

    public Task TakeOverAsync() => Call("agents.takeover");

    public Task GiveBackAsync() => Call("agents.release");

    public Task StopAsync() => Call("agents.stop");

    public async Task RefreshAsync()
    {
        if (_channel is null)
        {
            return;
        }

        try
        {
            var answer = await _channel.InvokeAsync("agents.tabs", JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
            var tabs = answer.GetProperty("tabs").EnumerateArray()
                .Select(item => new AgentTab(
                    item.GetProperty("paneId").GetString()!,
                    item.GetProperty("session").GetString()!,
                    item.GetProperty("targetId").GetString()!,
                    item.GetProperty("title").GetString()!,
                    item.GetProperty("url").GetString()!,
                    item.GetProperty("operatorDrives").GetBoolean(),
                    item.GetProperty("faviconUrl").GetString() ?? string.Empty))
                .ToList();
            Dispatcher.UIThread.Post(() => Apply(tabs));
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or JsonException)
        {
            Dispatcher.UIThread.Post(() => Fail(exception.Message));
        }
    }

    // A scene's tabs: what the backend would have listed, set at once, and the picture its screencast would show.
    internal void Stage(IEnumerable<AgentTab> tabs, Bitmap? frame = null, (double Width, double Height) pageSize = default)
    {
        _tabs = [.. tabs];
        _active = _tabs.FirstOrDefault();
        Frame = frame;
        PageSize = pageSize;
        Badge.Count = _tabs.Count;
    }

    public async Task SendInputAsync(string method, object parameters)
    {
        if (_active is not { } tab || _channel is null)
        {
            return;
        }

        try
        {
            await _channel.InvokeAsync("agents.input", JsonSerializer.SerializeToElement(new { paneId = tab.PaneId, targetId = tab.TargetId, method, @params = parameters })).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            // A dropped event is not worth a message; the next one goes through.
        }
    }

    public void Dispose()
    {
        _poll.Stop();
        foreach (var subscription in _subscriptions)
        {
            subscription.Dispose();
        }

        _ = Unwatch();
    }

    private void Apply(List<AgentTab> tabs)
    {
        Error = null;
        _tabs = tabs;
        Badge.Count = tabs.Count;
        _active = tabs.FirstOrDefault(tab => _active is not null && tab.PaneId == _active.PaneId && tab.TargetId == _active.TargetId) ?? tabs.FirstOrDefault();
        Changed?.Invoke();
        _ = SyncWatchAsync();
    }

    private void Fail(string message)
    {
        Error = message;
        Changed?.Invoke();
    }

    // One screencast, of the active tab, while the view shows; none when nobody looks.
    private async Task SyncWatchAsync()
    {
        var wanted = _viewing && _active is not null ? $"{_active.PaneId}/{_active.TargetId}" : null;
        if (wanted == _watching)
        {
            return;
        }

        _watching = wanted;
        if (wanted is null || _active is not { } tab)
        {
            await Unwatch().ConfigureAwait(false);
            return;
        }

        if (_channel is not null)
        {
            try
            {
                await _channel.InvokeAsync("agents.watch", JsonSerializer.SerializeToElement(new { paneId = tab.PaneId, targetId = tab.TargetId })).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidOperationException or JsonException)
            {
                _watching = null;
            }
        }
    }

    private async Task Unwatch()
    {
        _watching = null;
        if (_channel is null)
        {
            return;
        }

        try
        {
            await _channel.InvokeAsync("agents.unwatch", JsonSerializer.SerializeToElement(new { })).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            // Gone with the backend.
        }
    }

    private async Task Call(string action)
    {
        if (_active is not { } tab || _channel is null)
        {
            return;
        }

        try
        {
            await _channel.InvokeAsync(action, JsonSerializer.SerializeToElement(new { paneId = tab.PaneId })).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            Dispatcher.UIThread.Post(() => Fail(exception.Message));
        }
    }

    // Latest frame wins: a frame that arrives while the last one is still being decoded is dropped.
    private void OnFrame(Cockpit.Plugins.Abstractions.Channels.PluginChannelEvent channelEvent)
    {
        if (Interlocked.CompareExchange(ref _frameDecoding, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var payload = channelEvent.Payload;
            if (payload.GetProperty("paneId").GetString() != _active?.PaneId || payload.GetProperty("targetId").GetString() != _active?.TargetId)
            {
                return;
            }

            using var stream = new MemoryStream(Convert.FromBase64String(payload.GetProperty("data").GetString()!));
            var bitmap = new Bitmap(stream);
            var size = (payload.GetProperty("width").GetDouble(), payload.GetProperty("height").GetDouble());
            Dispatcher.UIThread.Post(() =>
            {
                Frame = bitmap;
                PageSize = size;
                FrameChanged?.Invoke();
            });
        }
        catch (Exception exception) when (exception is FormatException or KeyNotFoundException or InvalidOperationException or JsonException)
        {
            // A frame that does not decode is skipped.
        }
        finally
        {
            Interlocked.Exchange(ref _frameDecoding, 0);
        }
    }
}
