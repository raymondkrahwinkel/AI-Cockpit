using System.Text.Json;

namespace Cockpit.Plugin.Slack.Contracts;

// AC-1394: what the UI part tells the backend part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class SlackChannel
{
    // Payload: ignored. Told after the operator saves this plugin's settings (the UI part owns that dialog), so
    // the backend part can rebuild its Slack connection from whatever is now in storage — see
    // SlackChannelPlugin's own _Reconnect.
    public const string SettingsSaved = "settings-saved";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
