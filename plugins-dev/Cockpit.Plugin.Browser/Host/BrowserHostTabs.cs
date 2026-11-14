using System.Collections.Concurrent;
using System.Text.Json;
using Cockpit.Plugin.Browser.Ipc;
using Xilium.CefGlue;

namespace Cockpit.Plugin.Browser.Host;

internal sealed class BrowserHostTabs(BrowserIpcConnection connection, string storage, string cache) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, BrowserHostTab> _tabs = new();
    private CefRequestContext? _profile;

    internal async Task<JsonElement> HandleAsync(BrowserIpcMessage message, CancellationToken token)
    {
        if (message.Operation == "tab.create" && message.TabId == message.Generation)
        {
            var create = message.Payload.Deserialize<BrowserTabCreate>() ?? throw new InvalidDataException("Missing browser tab.");
            if (create.TabId == Guid.Empty || _tabs.ContainsKey(create.TabId) || _tabs.Count >= BrowserIpcLimits.Tabs ||
                Uri.TryCreate(create.Url, UriKind.Absolute, out _) == false)
            {
                throw new InvalidDataException("Invalid browser tab.");
            }
            connection.RegisterTab(create.TabId, "operator");
            var tab = new BrowserHostTab(connection, storage, create.TabId, create.Url);
            _tabs.TryAdd(create.TabId, tab);
            try
            {
                await BrowserHostTab.OnCefAsync(() =>
                {
                    _profile ??= CefRequestContext.CreateContext(new CefRequestContextSettings
                    {
                        CachePath = Path.Combine(cache, "user"), PersistSessionCookies = true,
                    }, null);
                    tab.Create(_profile, new BrowserTabSize(create.Width, create.Height, create.Scale, 1));
                }, token);
                await tab.Created.WaitAsync(token);
            }
            catch
            {
                _tabs.TryRemove(create.TabId, out _);
                try
                {
                    await tab.DisposeAsync();
                }
                finally
                {
                    await connection.UnregisterTabAsync(create.TabId, "operator");
                }
                throw;
            }
        }
        else if (message.Operation == "tab.close" && message.TabId == message.Generation)
        {
            var id = message.Payload.GetGuid();
            if (_tabs.TryRemove(id, out var tab))
            {
                try
                {
                    await tab.DisposeAsync();
                }
                finally
                {
                    await connection.UnregisterTabAsync(id, "operator");
                }
            }
        }
        else if (_tabs.TryGetValue(message.TabId, out var tab))
        {
            switch (message.Operation)
            {
                case "tab.devtools": return await tab.DevToolsAsync(message, token);
                case "tab.control": return tab.Control(message);
                case "tab.agent": return await tab.AgentAsync(message, token);
                case "tab.frames": return await tab.FramesAsync(token);
                case "tab.release": return await tab.ReleaseAsync(message, token);
                default: await tab.HandleAsync(message, token); break;
            }
        }
        else
        {
            throw new InvalidDataException("Unknown browser tab operation.");
        }
        return JsonSerializer.SerializeToElement(true);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var tab in _tabs.Values)
        {
            await tab.DisposeAsync();
        }
        _tabs.Clear();
        _profile?.Dispose();
    }
}
