using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Abstractions.TranscriptDisplay;
using Cockpit.Core.Abstractions.SessionBehavior;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Notifications;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The startup pending-approval banner (AC-208): plugins sitting at awaiting-approval — new, or their bytes
/// changed since last approved — otherwise showed up nowhere except a row in Plugin Manager. Drives the public
/// <see cref="CockpitViewModel.RefreshPluginFailures"/> against a real <see cref="PluginDiagnostics"/>, the same
/// seam <c>App.axaml.cs</c> calls after plugin phase-2 completes — mirrors
/// <c>CockpitViewModelUpdateBannerTests</c> for the sibling plugin-failure banner.
/// </summary>
public class CockpitViewModelPendingApprovalBannerTests
{
    [Fact]
    public void NoPendingApprovals_BannerStaysHidden()
    {
        var diagnostics = new PluginDiagnostics();
        var vm = NewVm(diagnostics);

        vm.RefreshPluginFailures();

        vm.HasPendingApprovals.Should().BeFalse();
        vm.PendingApprovalBanner.Should().BeEmpty();
    }

    [Fact]
    public void OnePendingApproval_BannerNamesIt()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.RecordPendingApproval("git-status", "Git Status");
        var vm = NewVm(diagnostics);

        vm.RefreshPluginFailures();

        vm.HasPendingApprovals.Should().BeTrue();
        vm.PendingApprovalBanner.Should().Contain("Git Status");
        vm.PendingApprovalBanner.Should().Contain("Plugin store");
    }

    [Fact]
    public void SeveralPendingApprovals_BannerShowsTheCount()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.RecordPendingApproval("git-status", "Git Status");
        diagnostics.RecordPendingApproval("clock", "Clock");
        var vm = NewVm(diagnostics);

        vm.RefreshPluginFailures();

        vm.HasPendingApprovals.Should().BeTrue();
        vm.PendingApprovalBanner.Should().Contain("2 plugins");
    }

    [Fact]
    public void Dismiss_HidesTheBanner()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.RecordPendingApproval("git-status", "Git Status");
        var vm = NewVm(diagnostics);
        vm.RefreshPluginFailures();

        vm.DismissPendingApprovalsCommand.Execute(null);

        vm.HasPendingApprovals.Should().BeFalse();
    }

    [Fact]
    public void PendingApproval_IsNotCountedAsAPluginFailure()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.RecordPendingApproval("git-status", "Git Status");
        var vm = NewVm(diagnostics);

        vm.RefreshPluginFailures();

        // Awaiting approval is an everyday state, not a load failure — the two banners are independent.
        vm.HasPluginFailures.Should().BeFalse();
        vm.PluginFailureBanner.Should().BeEmpty();
    }

    private static CockpitViewModel NewVm(PluginDiagnostics diagnostics)
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
            pluginDiagnostics: diagnostics);
    }
}
