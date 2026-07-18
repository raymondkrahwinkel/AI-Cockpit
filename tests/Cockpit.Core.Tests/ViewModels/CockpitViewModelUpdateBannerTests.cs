using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Updates;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Notifications;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Updates;
using Cockpit.Core.Voice;
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
            .Returns(new UpdateCheckResult(Release("1.2.3", "abc", "v1.2.3"), null));
        var vm = NewVm(updates);

        await vm.CheckForUpdatesAsync();

        vm.HasUpdate.Should().BeTrue();
        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("v1.2.3");
    }

    [Fact]
    public async Task Dismiss_HidesTheBanner_ButLeavesTheUpdateAvailable()
    {
        var updates = Substitute.For<IUpdateService>();
        updates.CheckAsync(Arg.Any<UpdateChannel>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateCheckResult(Release("1.2.3", "abc", "v1.2.3"), null));
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
            .Returns(new UpdateCheckResult(Release("1.2.3", "abc", "v1.2.3"), null));
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
                new UpdateCheckResult(Release("1.2.3", "abc", "v1.2.3"), null),
                new UpdateCheckResult(Release("1.2.4", "def", "v1.2.4"), null));
        var vm = NewVm(updates);
        await vm.CheckForUpdatesAsync();
        vm.DismissUpdateCommand.Execute(null);

        await vm.CheckForUpdatesAsync();

        vm.UpdateBannerVisible.Should().BeTrue();
        vm.UpdateName.Should().Be("v1.2.4");
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

    private static AppRelease Release(string version, string commit, string name) =>
        new(version, commit, name, "notes", $"https://example.test/{version}.{commit}", DateTimeOffset.UnixEpoch, IsPrerelease: false);

    private static CockpitViewModel NewVm(IUpdateService updates)
    {
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());

        var dialogService = Substitute.For<ISessionDialogService>();

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new TtyViewModel(),
            dialogService,
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            updateService: updates);
    }
}
