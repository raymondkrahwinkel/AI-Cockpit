using Cockpit.Core.SessionBehavior;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of <see cref="SessionBehaviorSettings"/> in the <c>sessionBehavior</c> section of
/// <c>cockpit.json</c>.
/// </summary>
internal sealed class SessionBehaviorSettingsEntry
{
    public bool AutoCloseOnExit { get; set; }

    public static SessionBehaviorSettingsEntry FromDomain(SessionBehaviorSettings settings) => new()
    {
        AutoCloseOnExit = settings.AutoCloseOnExit,
    };

    public SessionBehaviorSettings ToDomain() => new()
    {
        AutoCloseOnExit = AutoCloseOnExit,
    };
}
