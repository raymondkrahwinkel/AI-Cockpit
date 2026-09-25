using System.Text.Json;
using Cockpit.Plugin.Discord.Contracts;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Discord.UI;

// The UI part of Discord (AC-1394): the settings view. The backend part, DiscordChannelPlugin, keeps the gateway
// connection and rebuilds it whenever this part reports a save over the plugin's channel.
public sealed class DiscordUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new DiscordChannelSettings(host.Storage);
        host.AddSettings(() => new DiscordChannelSettingsControl(host, settings), "Assistant Plugins");

        // ponytail: OnSettingsSaved's callback is a synchronous Action (IPluginSettingsView predates the plugin
        // split), so it cannot await the channel call that tells the backend part to reconnect. Blocking is safe
        // today (the channel answers in-process with no thread hop); a fire-and-forget InvokeAsync would silently
        // swallow a failed reconnect. Upgrade path: a Task-returning ICockpitUiHost.OnSettingsSaved, tracked for
        // every plugin, not just this one.
        host.OnSettingsSaved(() => host.Channel.InvokeAsync(
            DiscordChannel.SettingsSaved, JsonSerializer.SerializeToElement<object?>(null, DiscordChannel.Json)).GetAwaiter().GetResult());
    }
}
