using Cockpit.Core.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// Maps a plugin's `PluginConversationId` to the core's own mirror, `SessionConversationId` (AC-408) — the
// conversion both `PluginSessionDriverAdapter` and `PluginTtySessionProviderAdapter` need before calling
// `ISessionConversationSink.Report`, since Cockpit.Core has no reference to the plugin contract assembly.
internal static class SessionConversationIdMapping
{
    public static SessionConversationId ToCore(this PluginConversationId conversation) => conversation.State switch
    {
        PluginConversationIdState.Known => SessionConversationId.Known(conversation.Value!),
        PluginConversationIdState.Unsupported => SessionConversationId.Unsupported,
        _ => SessionConversationId.Unknown,
    };
}
