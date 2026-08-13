using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// <see cref="ManagedCliUpdateChecker"/> (AC-20/AC-767): with auto-update off for a CLI, behaves exactly as
/// before — toasts once that a newer version exists and leaves installing to the config view's button. With it on
/// (the default), installs the newer version itself and toasts what changed; a failed install falls back to the
/// plain "available" toast. Never installs a CLI that is not already on disk, and two overlapping ticks never run
/// at once.
/// </summary>
public sealed class ManagedCliUpdateCheckerTests
{
    private readonly IToastService _toast = Substitute.For<IToastService>();

    [Fact]
    public async Task AutoUpdateOff_UpdateAvailable_ToastsOnce_ThenDedupsOnTheNextTick_AndNeverInstalls()
    {
        var managedCli = _ManagedCli(new ManagedCliStatus("2.1.212", "2.1.213"));
        var checker = _Checker(managedCli, autoUpdateEnabled: false);

        await checker.CheckNowAsync();
        await checker.CheckNowAsync();

        _toast.Received(1).Show(
            Arg.Is<string>(message => message.Contains("available") && message.Contains("2.1.213")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
        await managedCli.DidNotReceive().EnsureInstalledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AutoUpdateOff_UpToDate_DoesNotToast()
    {
        await _Checker(_ManagedCli(new ManagedCliStatus("2.1.213", "2.1.213")), autoUpdateEnabled: false).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task NotInstalled_DoesNothing_RegardlessOfFlag()
    {
        var managedCli = _ManagedCli(new ManagedCliStatus(null, "2.1.213"));

        await _Checker(managedCli, autoUpdateEnabled: true).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
        await managedCli.DidNotReceive().EnsureInstalledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChannelUnreachable_DoesNotToast()
    {
        await _Checker(_ManagedCli(new ManagedCliStatus("2.1.212", null)), autoUpdateEnabled: true).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task AutoUpdateOn_UpdateAvailable_InstallsAndToastsWhatChanged()
    {
        var managedCli = _ManagedCli(new ManagedCliStatus("2.1.212", "2.1.213"));
        managedCli.EnsureInstalledAsync("claude", Arg.Any<CancellationToken>())
            .Returns(ManagedCliInstallResult.Ok("2.1.213", @"C:\cli\claude\2.1.213\claude.exe"));

        await _Checker(managedCli, autoUpdateEnabled: true).CheckNowAsync();

        await managedCli.Received(1).EnsureInstalledAsync("claude", Arg.Any<CancellationToken>());
        _toast.Received(1).Show(
            Arg.Is<string>(message => message.Contains("2.1.212") && message.Contains("2.1.213")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
    }

    [Fact]
    public async Task AutoUpdateOn_InstallFails_FallsBackToAvailableToast()
    {
        var managedCli = _ManagedCli(new ManagedCliStatus("2.1.212", "2.1.213"));
        managedCli.EnsureInstalledAsync("claude", Arg.Any<CancellationToken>())
            .Returns(ManagedCliInstallResult.Fail("offline"));

        await _Checker(managedCli, autoUpdateEnabled: true).CheckNowAsync();

        _toast.Received(1).Show(
            Arg.Is<string>(message => message.Contains("available") && message.Contains("2.1.213")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
    }

    [Fact]
    public async Task OverlappingTick_SkipsWhileAPassIsStillRunning()
    {
        var managedCli = Substitute.For<IManagedCliService>();
        managedCli.RegisteredCliNames.Returns(new[] { "claude" });
        var statusGate = new TaskCompletionSource<ManagedCliStatus>();
        managedCli.GetStatusAsync("claude", Arg.Any<CancellationToken>()).Returns(_ => statusGate.Task);

        var checker = _Checker(managedCli, autoUpdateEnabled: true);

        var firstPass = checker.CheckNowAsync();
        var secondPass = checker.CheckNowAsync(); // lands while the first is still awaiting GetStatusAsync

        statusGate.SetResult(new ManagedCliStatus("2.1.212", "2.1.212")); // up to date, so the first pass just finishes
        await Task.WhenAll(firstPass, secondPass);

        await managedCli.Received(1).GetStatusAsync("claude", Arg.Any<CancellationToken>());
    }

    private static IManagedCliService _ManagedCli(ManagedCliStatus status)
    {
        var managedCli = Substitute.For<IManagedCliService>();
        managedCli.RegisteredCliNames.Returns(new[] { "claude" });
        managedCli.GetStatusAsync("claude", Arg.Any<CancellationToken>()).Returns(status);
        return managedCli;
    }

    private ManagedCliUpdateChecker _Checker(IManagedCliService managedCli, bool autoUpdateEnabled)
    {
        var autoUpdateStore = Substitute.For<IManagedCliAutoUpdateStore>();
        autoUpdateStore.IsEnabledAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(autoUpdateEnabled);
        return new ManagedCliUpdateChecker(managedCli, autoUpdateStore, _toast, NullLogger<ManagedCliUpdateChecker>.Instance);
    }
}
