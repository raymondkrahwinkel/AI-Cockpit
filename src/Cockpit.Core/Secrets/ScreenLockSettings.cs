namespace Cockpit.Core.Secrets;

// The operator's setting for AC-5: whether AI-Cockpit locks itself when the operating system locks. Persisted in
// its own `ScreenLock` section of `cockpit.json`, apart from the crypto `Security` section, so it
// survives turning encryption off and on again and a password change — the same lifecycle-independence reason the
// AC-41 `SecurityNotice` section is kept separate from the key material.
public sealed record ScreenLockSettings
{
    // Whether a screen lock should lock the cockpit too. On by default: encryption exists to keep the credentials
    // safe when the machine is away from the operator, and a lock is exactly that moment. It only takes effect
    // while encryption is on (there is nothing to re-ask for otherwise), and it is here so the operator can turn
    // it off without turning encryption off.
    public bool LockWhenOperatingSystemLocks { get; init; } = true;
}
