using Cockpit.Core.Diagnostics;
using Cockpit.Core.SessionBehavior;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `SessionBehaviorSettings` in the `sessionBehavior` section of
// `cockpit.json`.
internal sealed class SessionBehaviorSettingsEntry
{
    public bool AutoCloseOnExit { get; set; }

    public bool CombineQueuedMessages { get; set; }

    // Initialised to true, so a cockpit.json written before AC-615 — which has no such key — reads back as the
    // default rather than as the operator having said no. A bool that defaults to false on absence would have
    // turned "this setting did not exist yet" into "wake is off", silently, for every existing install.
    public bool WakeAgentsByDefault { get; set; } = true;

    // AC-1086: same reason as the line above, and sharper for a number — absent would read as a budget of zero,
    // which warns on an idle cockpit for every install that predates this setting.
    public int MemoryBudgetPercent { get; set; } = MemoryPressure.DefaultBudgetPercent;

    // AC-2044: off on absence — the cockpit prunes Docker only when the user turns this on.
    public bool AutomaticDockerCleanup { get; set; } = false;

    public static SessionBehaviorSettingsEntry FromDomain(SessionBehaviorSettings settings) => new()
    {
        AutomaticDockerCleanup = settings.AutomaticDockerCleanup,
        AutoCloseOnExit = settings.AutoCloseOnExit,
        CombineQueuedMessages = settings.CombineQueuedMessages,
        WakeAgentsByDefault = settings.WakeAgentsByDefault,
        MemoryBudgetPercent = settings.MemoryBudgetPercent,
    };

    public SessionBehaviorSettings ToDomain() => new()
    {
        AutomaticDockerCleanup = AutomaticDockerCleanup,
        AutoCloseOnExit = AutoCloseOnExit,
        CombineQueuedMessages = CombineQueuedMessages,
        WakeAgentsByDefault = WakeAgentsByDefault,
        MemoryBudgetPercent = MemoryBudgetPercent,
    };
}
