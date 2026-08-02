using Cockpit.Core.SessionBehavior;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `SessionBehaviorSettings` in the `sessionBehavior` section of
// `cockpit.json`.
internal sealed class SessionBehaviorSettingsEntry
{
    public bool AutoCloseOnExit { get; set; }

    public bool CombineQueuedMessages { get; set; }

    public static SessionBehaviorSettingsEntry FromDomain(SessionBehaviorSettings settings) => new()
    {
        AutoCloseOnExit = settings.AutoCloseOnExit,
        CombineQueuedMessages = settings.CombineQueuedMessages,
    };

    public SessionBehaviorSettings ToDomain() => new()
    {
        AutoCloseOnExit = AutoCloseOnExit,
        CombineQueuedMessages = CombineQueuedMessages,
    };
}
