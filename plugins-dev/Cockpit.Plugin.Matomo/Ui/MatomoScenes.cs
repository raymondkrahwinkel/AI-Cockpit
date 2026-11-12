using System.Text.Json;
using Avalonia.Controls;
using Cockpit.Plugin.Matomo.Contracts;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Matomo.UI;

internal static class MatomoScenes
{
    private static readonly MatomoSite[] Sites = [new(1, "Shop"), new(2, "Blog"), new(3, "Docs")];

    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>
    {
        ["matomo-not-connected"] = () => _View(null, null),
        ["matomo-connected"] = () => _View(new MatomoTestAnswer(MatomoStatus.Connected, "Connected to Matomo 5.14.2 with 3 site(s).", "5.14.2", Sites), [Sites[0], Sites[1]]),
        ["matomo-token-rejected"] = () => _View(new MatomoTestAnswer(MatomoStatus.TokenRejected, "Matomo rejected the token. Create a new token for a user with view access in Matomo (Personal › Security) and enter it in the plugin settings.", null, []), null),
        ["matomo-unreachable"] = () => _View(new MatomoTestAnswer(MatomoStatus.Unreachable, "Matomo at matomo.example.com did not answer (HttpRequestException). Check the URL, that the server is running and reachable from this machine.", null, []), null),
    };

    private static Control _View(MatomoTestAnswer? shown, MatomoSite[]? offered)
    {
        var storage = new SceneStorage();
        storage.Set(MatomoKeys.Url, shown is null ? string.Empty : "https://matomo.example.com");
        storage.Set(MatomoKeys.McpEnabled, shown is not null);
        storage.Set(MatomoKeys.OfferedSites, offered?.ToList() ?? []);
        storage.Set(MatomoKeys.DefaultSite, offered is null ? (int?)null : 1);
        return new MatomoSettingsControl(storage, new SceneChannel(), shown) { Margin = new(16), Width = 560 };
    }

    private sealed class SceneStorage : IPluginStorage
    {
        private readonly Dictionary<string, object?> _values = [];

        public T? Get<T>(string key) => _values.TryGetValue(key, out var value) && value is T typed ? typed : default;

        public void Set<T>(string key, T value) => _values[key] = value;
    }

    private sealed class SceneChannel : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => throw new NotSupportedException();
    }
}
