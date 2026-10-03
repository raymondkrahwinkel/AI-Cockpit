namespace Cockpit.Infrastructure.Sessions;

// AC-1444: a start that reaches the launcher after the backend's stop closed it. The reason travels in the message, as
// `TtyLaunchRefusedException`'s does, so whoever asked is told why nothing started.
public sealed class CockpitStoppingException()
    : InvalidOperationException("This cockpit is stopping, so it starts no new session.");
