using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// <see cref="DevPluginReloadWatcher"/> (AC-185): a rebuilt plugin under a <c>bin/</c> output offers one toast
/// action, whose click re-runs the install and only then restarts; a write outside <c>bin/</c> (the constant
/// <c>obj/</c> churn of a build in progress) is ignored, and an install failure never reaches the restart. The
/// debounce is passed as a synchronous no-op (<c>callback => callback()</c>) so these run without a dispatcher
/// or real wall-clock waits — the same seam style as <see cref="AppRestartService"/>.
/// </summary>
public class DevPluginReloadWatcherTests
{
    // Built with Path.Combine, not a literal Windows path — the "bin/" check in production code keys off
    // Path.DirectorySeparatorChar, which is '/' on the Linux CI runner, so a hardcoded backslash path here
    // would silently never match there.
    private static readonly string PluginsDevRoot = Path.Combine("repo", "plugins-dev");
    private static readonly string BuiltDll = Path.Combine(PluginsDevRoot, "Cockpit.Plugin.GitStatus", "bin", "Debug", "net10.0", "Cockpit.Plugin.GitStatus.dll");
    private static readonly string ObjDll = Path.Combine(PluginsDevRoot, "Cockpit.Plugin.GitStatus", "obj", "Debug", "net10.0", "Cockpit.Plugin.GitStatus.dll");

    [Fact]
    public void Start_OffADevCheckout_DoesNothing()
    {
        var (watcher, _, _) = _Create(pluginsDevRoot: null);

        var start = () => watcher.Start();

        start.Should().NotThrow();
        watcher.Dispose();
    }

    [Fact]
    public void ARebuiltDllUnderBin_OffersOneReloadToast()
    {
        var (watcher, toast, _) = _Create(PluginsDevRoot);

        watcher.SimulateBuildOutputChangedForTests(BuiltDll);

        toast.Received(1).Show("A dev plugin was rebuilt.", ToastSeverity.Information, "Reload", Arg.Any<Action>());
    }

    [Fact]
    public void AWriteUnderObj_IsIgnored()
    {
        var (watcher, toast, _) = _Create(PluginsDevRoot);

        watcher.SimulateBuildOutputChangedForTests(ObjDll);

        toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task ClickingReload_InstallsThenRestarts()
    {
        var installCalls = 0;
        var (watcher, toast, restart) = _Create(PluginsDevRoot, installAsync: _ =>
        {
            installCalls++;
            return Task.FromResult<IReadOnlyList<string>>(["git-status"]);
        });
        watcher.SimulateBuildOutputChangedForTests(BuiltDll);
        var onAction = _CapturedAction(toast);

        onAction();
        await Task.Yield(); // let the fire-and-forget reload continuation run

        installCalls.Should().Be(1);
        restart.Received(1).Restart();
    }

    [Fact]
    public async Task AFailedInstall_NeverRestarts_AndToastsTheFailure()
    {
        var (watcher, toast, restart) = _Create(PluginsDevRoot, installAsync: _ =>
            throw new InvalidOperationException("locked file"));
        watcher.SimulateBuildOutputChangedForTests(BuiltDll);
        var onAction = _CapturedAction(toast);

        onAction();
        await Task.Yield();

        restart.DidNotReceive().Restart();
        toast.Received(1).Show(Arg.Is<string>(message => message.Contains("failed")), ToastSeverity.Error, Arg.Any<string?>(), Arg.Any<Action?>());
    }

    // Pulled from the recorded call rather than an Arg.Do inside Received() — NSubstitute only runs an Arg.Do
    // callback while the call is being matched during invocation, not while re-verifying it afterwards.
    private static Action _CapturedAction(IToastService toast)
    {
        var show = toast.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(IToastService.Show));
        var onAction = (Action?)show.GetArguments()[3];

        onAction.Should().NotBeNull();
        return onAction!;
    }

    private static (DevPluginReloadWatcher Watcher, IToastService Toast, IAppRestartService Restart) _Create(
        string? pluginsDevRoot,
        Func<CancellationToken, Task<IReadOnlyList<string>>>? installAsync = null)
    {
        var toast = Substitute.For<IToastService>();
        var restart = Substitute.For<IAppRestartService>();

        var watcher = new DevPluginReloadWatcher(
            resolvePluginsDevRoot: () => pluginsDevRoot,
            installAsync: installAsync ?? (_ => Task.FromResult<IReadOnlyList<string>>([])),
            toast: toast,
            restartService: restart,
            logger: NullLogger<DevPluginReloadWatcher>.Instance,
            debounce: callback => callback());

        return (watcher, toast, restart);
    }
}
