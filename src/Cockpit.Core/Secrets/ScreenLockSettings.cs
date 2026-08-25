namespace Cockpit.Core.Secrets;

// The operator's setting for AC-5: whether AI-Cockpit locks itself when the operating system locks. Kept in its
// own `ScreenLock` section, apart from the crypto `Security` section, so it survives turning encryption
// off and on again and a password change.
public sealed record ScreenLockSettings
{
    // Whether a screen lock should lock the cockpit too. On by default, since a lock is exactly the moment
    // encryption exists to protect. Only takes effect while encryption is on.
    public bool LockWhenOperatingSystemLocks { get; init; } = true;
}
