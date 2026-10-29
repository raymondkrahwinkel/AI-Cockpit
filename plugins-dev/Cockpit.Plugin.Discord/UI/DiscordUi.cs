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

        // Awaited by the host (AC-1891), so a failed reconnect is not swallowed.
        host.OnSettingsSaved(() => host.Channel.InvokeAsync(
            DiscordChannel.SettingsSaved, JsonSerializer.SerializeToElement<object?>(null, DiscordChannel.Json)));
    }
}
