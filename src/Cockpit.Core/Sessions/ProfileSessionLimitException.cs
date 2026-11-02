namespace Cockpit.Core.Sessions;

// AC-2026: a start refused because its profile already runs its maximum of parallel sessions; the message is the reason.
public sealed class ProfileSessionLimitException(string reason) : InvalidOperationException(reason);
