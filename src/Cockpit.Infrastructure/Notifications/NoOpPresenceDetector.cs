using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Notifications;

namespace Cockpit.Infrastructure.Notifications;

/// <summary>
/// Non-Windows fallback presence detector. FOLLOW-UP / NOT IMPLEMENTED: Linux/Wayland idle + lock
/// detection (logind/D-Bus; Wayland idle APIs are restrictive) is not built yet. Rather than fail
/// silently, this always reports <see cref="PresenceState.Present"/> — so on a non-Windows host the
/// router picks the toast channel and never routes to the away/webhook path on stale presence data.
/// The webhook branch therefore stays inert on Linux until a real detector lands. Registered only on
/// non-Windows platforms (see DI); on Windows <c>WindowsPresenceDetector</c> is used instead.
/// </summary>
internal sealed class NoOpPresenceDetector : IPresenceDetector
{
    public PresenceState GetPresence(TimeSpan idleThreshold) => PresenceState.Present;
}
