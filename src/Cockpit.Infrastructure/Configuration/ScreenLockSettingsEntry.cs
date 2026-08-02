using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

// On-disk shape of `ScreenLockSettings` in the `ScreenLock` section of `cockpit.json`.
internal sealed class ScreenLockSettingsEntry
{
    // Defaults to on so a config that never wrote this section still locks with the OS — the same default the store returns when the section is absent.
    public bool LockWhenOperatingSystemLocks { get; set; } = true;

    public static ScreenLockSettingsEntry FromDomain(ScreenLockSettings settings) => new()
    {
        LockWhenOperatingSystemLocks = settings.LockWhenOperatingSystemLocks,
    };

    public ScreenLockSettings ToDomain() => new()
    {
        LockWhenOperatingSystemLocks = LockWhenOperatingSystemLocks,
    };
}
