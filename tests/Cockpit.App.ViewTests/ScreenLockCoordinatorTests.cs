using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The AC-5 gate, tested where it can be: the coordinator decides — per screen-lock event — whether to lock the app's
/// UI, and shows the unlock screen only when it does. The native monitor that reports the OS lock cannot be
/// unit-tested (it needs a real desktop lock), so it is faked here; what is proven is the part that matters for
/// correctness — that a lock happens only with encryption on and the option on, and that a duplicate event does not
/// stack a second unlock screen. This is a pure UI lock: the coordinator never clears the key (that a running agent's
/// write survives is pinned in the infrastructure tests), so there is nothing key-related to assert here.
/// </summary>
public class ScreenLockCoordinatorTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task ItLocksTheUi_OnlyWhenEncryptionAndTheOptionAreOn(bool encryption, bool optionOn, bool expectLock)
    {
        var protection = new FakeProtection { Enabled = encryption, Unlocked = encryption };
        var count = 0;

        // LockAction deliberately does not touch Unlocked here: it models nothing but showing the screen, so the
        // assertion below genuinely proves the coordinator left the key alone rather than the fake putting it back.
        var coordinator = new ScreenLockCoordinator(new FakeMonitor(), protection, new FakeSettings(optionOn), NullLogger<ScreenLockCoordinator>.Instance)
        {
            LockAction = () =>
            {
                Interlocked.Increment(ref count);

                return Task.CompletedTask;
            },
        };

        var locked = await coordinator.HandleLockAsync();

        Assert.Equal(expectLock, locked);
        Assert.Equal(expectLock ? 1 : 0, count);
        Assert.Equal(encryption, protection.Unlocked);
    }

    [Fact]
    public async Task ADuplicateEvent_WhileTheScreenIsUp_DoesNotStackASecondLock()
    {
        var protection = new FakeProtection { Enabled = true, Unlocked = true };
        var actionStarted = new TaskCompletionSource();
        var actionRelease = new TaskCompletionSource();
        var count = 0;

        var coordinator = new ScreenLockCoordinator(new FakeMonitor(), protection, new FakeSettings(true), NullLogger<ScreenLockCoordinator>.Instance)
        {
            LockAction = async () =>
            {
                Interlocked.Increment(ref count);
                actionStarted.TrySetResult();
                await actionRelease.Task;
            },
        };

        // First event enters, clears the key and shows the screen — held there by the un-signalled release.
        var first = coordinator.HandleLockAsync();
        await actionStarted.Task;

        // Second event arrives while the screen is still up: it must turn back rather than lock again.
        var second = await coordinator.HandleLockAsync();

        actionRelease.SetResult();

        Assert.True((await first));
        Assert.False(second, "a lock is already in effect");
        Assert.Equal(1, count);
    }

    private sealed class FakeMonitor : IScreenLockMonitor
    {
        public event EventHandler? Locked;

        public event EventHandler? Unlocked;

        public void RaiseLocked() => Locked?.Invoke(this, EventArgs.Empty);

        public void RaiseUnlocked() => Unlocked?.Invoke(this, EventArgs.Empty);

        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class FakeSettings(bool on) : IScreenLockSettingsStore
    {
        public Task<ScreenLockSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScreenLockSettings { LockWhenOperatingSystemLocks = on });

        public Task SaveAsync(ScreenLockSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeProtection : ISecretProtectionService
    {
        public bool Enabled { get; set; }

        public bool Unlocked { get; set; }

        public Task<SecretProtectionStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretProtectionStatus(Enabled, Unlocked));

        public Task DismissUnprotectedWarningAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task EnableAsync(string password, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisableAsync(IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ChangePasswordAsync(string currentPassword, string newPassword, IProgress<SecretMigrationProgress>? progress = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResetForgottenPasswordAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
