using Cockpit.App.Services;
using Cockpit.Core.Abstractions.Toasts;
using Cockpit.Core.Toasts;
using Cockpit.Infrastructure.ManagedCli;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Services;

/// <summary>
/// <see cref="ManagedCliUpdateChecker"/> (AC-20): toasts once when an installed managed CLI has a newer version, and
/// says nothing when up to date, not installed, or the channel could not be reached.
/// </summary>
public sealed class ManagedCliUpdateCheckerTests
{
    private readonly IToastService _toast = Substitute.For<IToastService>();

    [Fact]
    public async Task UpdateAvailable_ToastsOnce_ThenDedupsOnTheNextTick()
    {
        var checker = _Checker(new ManagedCliStatus("2.1.212", "2.1.213"));

        await checker.CheckNowAsync();
        await checker.CheckNowAsync();

        _toast.Received(1).Show(
            Arg.Is<string>(message => message.Contains("2.1.213")),
            ToastSeverity.Information,
            Arg.Any<string?>(),
            Arg.Any<Action?>());
    }

    [Fact]
    public async Task UpToDate_DoesNotToast()
    {
        await _Checker(new ManagedCliStatus("2.1.213", "2.1.213")).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task NotInstalled_DoesNotToast()
    {
        await _Checker(new ManagedCliStatus(null, "2.1.213")).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    [Fact]
    public async Task ChannelUnreachable_DoesNotToast()
    {
        await _Checker(new ManagedCliStatus("2.1.212", null)).CheckNowAsync();

        _toast.DidNotReceiveWithAnyArgs().Show(default!, default, default, default);
    }

    private ManagedCliUpdateChecker _Checker(ManagedCliStatus status)
    {
        var managedCli = Substitute.For<IManagedCliService>();
        managedCli.RegisteredCliNames.Returns(new[] { "claude" });
        managedCli.GetStatusAsync("claude", Arg.Any<CancellationToken>()).Returns(status);
        return new ManagedCliUpdateChecker(managedCli, _toast, NullLogger<ManagedCliUpdateChecker>.Instance);
    }
}
