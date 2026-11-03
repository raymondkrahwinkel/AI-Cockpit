using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Secrets;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Secrets;
using Cockpit.Core.Terminal;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The Options dialog as a transaction (AC-999). Nothing the operator changes reaches a store until Apply, and
/// Cancel leaves the cockpit exactly as the dialog found it.
///
/// The stores are watched *while* the values change, not only afterwards: the failure this replaces was a dialog
/// that wrote as you typed, where a test that only checked the end state would have passed on the way to it.
/// </summary>
public class OptionsStagedChangesTests
{
    [Fact]
    public async Task SecurityToggles_AreHeldBackWhileSuspended_AndWrittenOnApply()
    {
        var screenLock = Substitute.For<IScreenLockSettingsStore>();
        screenLock.LoadAsync().Returns(new ScreenLockSettings());
        var terminalAccess = Substitute.For<ITerminalAccessSettingsStore>();
        terminalAccess.LoadAsync().Returns(new TerminalAccessSettings());
        var security = new SecurityOptionsViewModel(
            Substitute.For<ISecretProtectionService>(),
            screenLock,
            terminalAccessSettings: terminalAccess)
        {
            SuspendPersistence = true,
        };

        security.LockWithOperatingSystem = false;
        security.TerminalAccessEnabled = true;

        await screenLock.DidNotReceive().SaveAsync(Arg.Any<ScreenLockSettings>());
        await terminalAccess.DidNotReceive().SaveAsync(Arg.Any<TerminalAccessSettings>());

        security.SuspendPersistence = false;
        await security.SaveStagedAsync();

        await screenLock.Received().SaveAsync(Arg.Is<ScreenLockSettings>(s => !s.LockWhenOperatingSystemLocks));
        await terminalAccess.Received().SaveAsync(Arg.Is<TerminalAccessSettings>(s => s.Enabled));
    }

    [Fact]
    public async Task SecurityToggles_ComeBackFromDiskOnCancel()
    {
        var screenLock = Substitute.For<IScreenLockSettingsStore>();
        screenLock.LoadAsync().Returns(new ScreenLockSettings { LockWhenOperatingSystemLocks = true });
        var security = new SecurityOptionsViewModel(Substitute.For<ISecretProtectionService>(), screenLock)
        {
            SuspendPersistence = true,
        };

        security.LockWithOperatingSystem = false;

        // AC-1286: Cancel's own re-seed, the one refresh that may overwrite a staged value — every other one
        // leaves it alone now, and the theory in SecurityOptionsViewModelTests holds that side.
        await security.RefreshAsync(reseedStagedValues: true);

        Assert.True(security.LockWithOperatingSystem);
        await screenLock.DidNotReceive().SaveAsync(Arg.Any<ScreenLockSettings>());
    }
}
