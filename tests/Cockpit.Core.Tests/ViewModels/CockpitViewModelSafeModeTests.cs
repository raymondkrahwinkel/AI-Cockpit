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
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The safe-mode banner (AC-478): the cockpit's only visible marker that this run skipped the plugin load phase
/// entirely (<see cref="PluginManager.SafeMode"/>). Mirrors <c>CockpitViewModelPendingApprovalBannerTests</c> for
/// the sibling banner — the view model reads the same <see cref="PluginManager"/> singleton
/// <c>Program.cs</c> constructs the switch on, not a second source of truth.
/// </summary>
public class CockpitViewModelSafeModeTests
{
    [Fact]
    public void NoPluginManager_SafeModeBannerStaysHidden()
    {
        var vm = NewVm(pluginManager: null);

        Assert.False(vm.IsSafeMode);
        Assert.Empty(vm.SafeModeBanner);
    }

    [Fact]
    public void OrdinaryRun_SafeModeBannerStaysHidden()
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(), safeMode: false);

        var vm = NewVm(manager);

        Assert.False(vm.IsSafeMode);
        Assert.Empty(vm.SafeModeBanner);
    }

    [Fact]
    public void SafeModeRun_BannerIsShownAndNamesSafeMode()
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(), safeMode: true);

        var vm = NewVm(manager);

        Assert.True(vm.IsSafeMode);
        Assert.Contains("Safe mode", vm.SafeModeBanner, StringComparison.Ordinal);
    }

    [Fact]
    public void SafeModeRun_WithNoRestartService_RestartButtonIsDisabled()
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(), safeMode: true);

        var vm = NewVm(manager, appRestartService: null);

        // The safe-mode banner's only way out is the same "Restart" affordance the rest of the app offers
        // (RestartAppCommand) — with no IAppRestartService (the design-time/unit-test graph), it must not
        // pretend a click would do anything.
        Assert.False(vm.CanRestartApp);
    }

    [Fact]
    public void SafeModeRun_WithARestartService_RestartButtonCallsIt()
    {
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(), safeMode: true);
        var restartService = Substitute.For<IAppRestartService>();

        var vm = NewVm(manager, restartService);

        Assert.True(vm.CanRestartApp);
        vm.RestartAppCommand.Execute(null);
        restartService.Received(1).Restart();
    }

    private static CockpitViewModel NewVm(PluginManager? pluginManager, IAppRestartService? appRestartService = null)
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
            appRestartService: appRestartService,
            pluginManager: pluginManager);
    }
}
