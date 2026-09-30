using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Security;

namespace Cockpit.Infrastructure.Tests.Security;

/// <summary>
/// The AC-5 option's persistence: on by default so a config that never wrote it still locks with the OS, and a
/// round-trip so turning it off actually sticks.
/// </summary>
public sealed class ScreenLockSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cockpit-screenlock-{Guid.NewGuid():N}");

    private string ConfigPath => Path.Combine(_directory, "cockpit.json");

    public ScreenLockSettingsStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultsToOn_WhenNothingWasSaved() =>
        Assert.True(
            (await new ScreenLockSettingsStore(ConfigPath).LoadAsync()).LockWhenOperatingSystemLocks,
            "locking with the OS is the default while encryption is on");

    [Fact]
    public async Task Save_RoundTripsTheChoice()
    {
        var store = new ScreenLockSettingsStore(ConfigPath);

        await store.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = false });
        Assert.False((await store.LoadAsync()).LockWhenOperatingSystemLocks, "the operator turned it off");

        await store.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = true });
        Assert.True((await store.LoadAsync()).LockWhenOperatingSystemLocks, "and back on again");
    }
}
