using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

// Non-Windows fallback presence detector. FOLLOW-UP / NOT IMPLEMENTED: Linux/Wayland idle + lock
// detection is not built yet, so this always reports `PresenceState.Present`, keeping the router on
// the toast channel and the webhook branch inert on Linux until a real detector lands.
internal sealed class NoOpPresenceDetector : IPresenceDetector
{
    public PresenceState GetPresence(TimeSpan idleThreshold) => PresenceState.Present;
}
