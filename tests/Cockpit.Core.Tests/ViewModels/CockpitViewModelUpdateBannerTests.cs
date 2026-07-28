using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Updates;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The persistent update banner (AC-73): a found build shows it, dismissing hides it for that build only, and a
/// genuinely newer build brings it back. Drives the public <see cref="CockpitViewModel.CheckForUpdatesAsync"/>
/// against a fake <see cref="IUpdateService"/> — the same seam the Options "Check now" button uses.
/// </summary>
public class CockpitViewModelUpdateBannerTests
{
    [Fact]
    public async Task WhenAnUpdateIsFound_TheBannerShowsWithTheReleaseName()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);

        await vm.CheckForUpdatesAsync();

        vm.HasUpdate.Should().BeTrue();
        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("1.2.3");
    }

    [Fact]
    public async Task Dismiss_HidesTheBanner_ButLeavesTheUpdateAvailable()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);
        await vm.CheckForUpdatesAsync();

        vm.DismissUpdateCommand.Execute(null);

        vm.UpdateBannerVisible.Should().BeFalse();
        // Dismiss is about the banner, not the fact of the update — the release is still there to open.
        vm.HasUpdate.Should().BeTrue();
    }

    [Fact]
    public async Task AfterDismiss_RecheckingTheSameBuild_LeavesTheBannerHidden()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);
        await vm.CheckForUpdatesAsync();
        vm.DismissUpdateCommand.Execute(null);

        await vm.CheckForUpdatesAsync();

        vm.UpdateBannerVisible.Should().BeFalse();
    }

    [Fact]
    public async Task AfterDismiss_ANewerBuild_BringsTheBannerBack()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(
                new UpdateCheckResult(Release("1.2.3"), null),
                new UpdateCheckResult(Release("1.2.4"), null));
        var vm = NewVm(updates);
        await vm.CheckForUpdatesAsync();
        vm.DismissUpdateCommand.Execute(null);

        await vm.CheckForUpdatesAsync();

        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("1.2.4");
    }

    [Fact]
    public async Task AFailedCheck_ShowsNoBanner()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(UpdateCheckResult.Failed("GitHub was unreachable."));
        var vm = NewVm(updates);

        await vm.CheckForUpdatesAsync();

        vm.UpdateBannerVisible.Should().BeFalse();
        vm.HasUpdate.Should().BeFalse();
    }

    [Fact]
    public async Task RunPeriodicUpdateCheck_WhenABuildIsFound_ShowsTheBannerAndRaisesExactlyOneToast()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);

        await vm.RunPeriodicUpdateCheckAsync();

        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("1.2.3");
        vm.Toasts.Should().ContainSingle();
    }

    [Fact]
    public async Task RunPeriodicUpdateCheck_TwiceForTheSameBuild_ToastsOnlyOnce_AndKeepsTheBannerVisible()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);

        await vm.RunPeriodicUpdateCheckAsync();
        await vm.RunPeriodicUpdateCheckAsync();

        // The same release must not nag every hour — one toast for the build, and the banner is still up.
        vm.Toasts.Should().ContainSingle();
        vm.UpdateBannerVisible.Should().BeTrue();
    }

    [Fact]
    public async Task RunPeriodicUpdateCheck_WhenStartupChecksAreOff_LooksAtNothing()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);
        // The startup setting is the global on/off for every automatic check — off means the hourly loop stays quiet too.
        vm.CheckForUpdatesOnStartup = false;

        await vm.RunPeriodicUpdateCheckAsync();

        await updates.DidNotReceive().CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>());
        vm.UpdateBannerVisible.Should().BeFalse();
        vm.Toasts.Should().BeEmpty();
    }

    [Fact]
    public async Task RunPeriodicUpdateCheck_WhenAGenuinelyNewerBuildArrives_ReToastsAndReShowsTheBanner()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(
                new UpdateCheckResult(Release("1.2.3"), null),
                new UpdateCheckResult(Release("1.2.4"), null));
        var vm = NewVm(updates);

        await vm.RunPeriodicUpdateCheckAsync();
        await vm.RunPeriodicUpdateCheckAsync();

        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("1.2.4");
        // A newer build is worth telling about again: one toast per distinct release.
        vm.Toasts.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunPeriodicUpdateCheck_AfterDismiss_DoesNotReToastOrReShowTheSameBuild()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3"), null));
        var vm = NewVm(updates);
        await vm.RunPeriodicUpdateCheckAsync();
        vm.DismissUpdateCommand.Execute(null);

        await vm.RunPeriodicUpdateCheckAsync();

        // Dismissed and unchanged: no new toast, and the banner stays down.
        vm.Toasts.Should().ContainSingle();
        vm.UpdateBannerVisible.Should().BeFalse();
    }

    private static AppRelease Release(string version) => new(version, "notes", $"https://example.test/{version}");

    private static CockpitViewModel NewVm(IUpdateService updates) => UpdateTestCockpit.Build(updates);
}
