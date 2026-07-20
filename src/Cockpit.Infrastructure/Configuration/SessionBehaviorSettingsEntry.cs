using Cockpit.Core.SessionBehavior;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="SessionBehaviorSettings"/> in the <c>sessionBehavior</c> section of
/// <c>cockpit.json</c>.
/// </summary>
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
