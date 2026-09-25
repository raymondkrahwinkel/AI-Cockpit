using System.Text.Json;

namespace Cockpit.Plugin.Discord.Contracts;

// AC-1394: what the UI part tells the backend part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class DiscordChannel
{
    // Payload: none. Tells the backend part the settings view was saved, so it rebuilds the Discord connection
    // from whatever is now in storage — the same rebuild DiscordChannelPlugin.Initialize's own host.OnSettingsSaved
    // callback used to run directly before AC-1394 split the settings view into the UI part. Answers nothing.
    public const string SettingsSaved = "settings-saved";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}
