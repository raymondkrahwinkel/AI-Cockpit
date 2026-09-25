using System.Text.Json;
using Cockpit.Plugin.Slack.Contracts;
using Cockpit.Plugin.Slack.Settings;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.Slack.UI;

// The UI part of Slack (AC-1394): the settings view. The backend part, SlackChannelPlugin, keeps the Socket Mode
// connection and reconnects it — at startup and whenever this part tells it a settings save happened.
public sealed class SlackChannelUi : ICockpitPluginUi
{
    public void InitializeUi(ICockpitUiHost host)
    {
        var settings = new SlackChannelSettings(host.Storage);
        host.AddSettings(() => new SlackChannelSettingsControl(host, settings), "Assistant Plugins");

        // ponytail: OnSettingsSaved's callback is a synchronous Action, so it cannot await; blocking is safe today
        // (the channel answers in-process with no thread hop).
        host.OnSettingsSaved(() =>
            host.Channel.InvokeAsync(SlackChannel.SettingsSaved, JsonSerializer.SerializeToElement(true, SlackChannel.Json)).GetAwaiter().GetResult());
    }
}
