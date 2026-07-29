using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Secrets;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The AC-5 option on the Security tab: it seeds from disk, persists the moment it changes, and — the subtle part —
/// the seed from disk is not itself written back out.
/// </summary>
public class ScreenLockOptionTests
{
    [Fact]
    public async Task RefreshAsync_SeedsTheToggleFromDisk_WithoutWritingItBack()
    {
        var store = new RecordingStore(loaded: false);
        var vm = new SecurityOptionsViewModel(new UnprotectedSecrets(), store);

        await vm.RefreshAsync();

        Assert.False(vm.LockWithOperatingSystem, "that is what the store held");
        Assert.Equal(0, store.Saves);
    }

    [Fact]
    public async Task TogglingTheOption_PersistsIt()
    {
        var store = new RecordingStore(loaded: true);
        var vm = new SecurityOptionsViewModel(new UnprotectedSecrets(), store);
        await vm.RefreshAsync();

        vm.LockWithOperatingSystem = false;

        Assert.Equal(1, store.Saves);
        Assert.False(store.LastSaved!.LockWhenOperatingSystemLocks, "the operator turned it off");
    }

    private sealed class RecordingStore(bool loaded) : IScreenLockSettingsStore
    {
        public int Saves { get; private set; }

        public ScreenLockSettings? LastSaved { get; private set; }

        public Task<ScreenLockSettings> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ScreenLockSettings { LockWhenOperatingSystemLocks = loaded });

        public Task SaveAsync(ScreenLockSettings settings, CancellationToken cancellationToken = default)
        {
            Saves++;
            LastSaved = settings;

            return Task.CompletedTask;
        }
    }
}
