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

    // Initialised to true, so a cockpit.json written before AC-615 — which has no such key — reads back as the
    // default rather than as the operator having said no. A bool that defaults to false on absence would have
    // turned "this setting did not exist yet" into "wake is off", silently, for every existing install.
    public bool WakeAgentsByDefault { get; set; } = true;

    public static SessionBehaviorSettingsEntry FromDomain(SessionBehaviorSettings settings) => new()
    {
        AutoCloseOnExit = settings.AutoCloseOnExit,
        CombineQueuedMessages = settings.CombineQueuedMessages,
        WakeAgentsByDefault = settings.WakeAgentsByDefault,
    };

    public SessionBehaviorSettings ToDomain() => new()
    {
        AutoCloseOnExit = AutoCloseOnExit,
        CombineQueuedMessages = CombineQueuedMessages,
        WakeAgentsByDefault = WakeAgentsByDefault,
    };
}
