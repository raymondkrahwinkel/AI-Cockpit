using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

// AC-1357: a backend without a frontend has nobody at its screen, so every attention notification takes the away
// channel — the Discord webhook when that is on. A toast there would only be a log line nobody reads.
internal sealed class AwayPresenceDetector : IPresenceDetector
{
    public PresenceState GetPresence(TimeSpan idleThreshold) => PresenceState.Away;
}
