using Cockpit.Core.Secrets;
using Cockpit.Infrastructure.Security;
using FluentAssertions;

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
        (await new ScreenLockSettingsStore(ConfigPath).LoadAsync())
            .LockWhenOperatingSystemLocks.Should().BeTrue("locking with the OS is the default while encryption is on");

    [Fact]
    public async Task Save_RoundTripsTheChoice()
    {
        var store = new ScreenLockSettingsStore(ConfigPath);

        await store.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = false });
        (await store.LoadAsync()).LockWhenOperatingSystemLocks.Should().BeFalse("the operator turned it off");

        await store.SaveAsync(new ScreenLockSettings { LockWhenOperatingSystemLocks = true });
        (await store.LoadAsync()).LockWhenOperatingSystemLocks.Should().BeTrue("and back on again");
    }
}
