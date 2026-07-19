using Cockpit.Core.Secrets;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>On-disk shape of <see cref="ScreenLockSettings"/> in the <c>ScreenLock</c> section of <c>cockpit.json</c>.</summary>
internal sealed class ScreenLockSettingsEntry
{
    /// <summary>Defaults to on so a config that never wrote this section still locks with the OS — the same default the store returns when the section is absent.</summary>
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
