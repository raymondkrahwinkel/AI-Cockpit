using Avalonia.Controls;
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
using Cockpit.Core.Profiles;
using Cockpit.Core.Terminal;
using Cockpit.Core.TranscriptDisplay;
using Cockpit.Core.SessionBehavior;
using Cockpit.Core.Layout;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="CockpitViewModel"/>'s session-manager surface (new/select/close) against a
/// fake session factory and a fake <see cref="ISessionDialogService"/>. Since #31 the app opens no
/// session on startup and creating one goes through the New-session dialog, so tests that need a panel
/// confirm one first via <c>NewSessionCommand</c> (the fake dialog returns a canned result).
/// </summary>
public class CockpitViewModelTests
{
    [Fact]
    public void Constructor_OpensNoSessionOnStartup()
    {
        var vm = NewVm();

        vm.Sessions.Should().BeEmpty();
        vm.HasSessions.Should().BeFalse();
        vm.SelectedSession.Should().BeNull();
    }

    [Fact]
    public async Task AboutCommand_ShowsTheAboutDialog()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService);

        await vm.AboutCommand.ExecuteAsync(null);

        await dialogService.Received(1).ShowAboutDialogAsync();
    }

    [Fact]
    public async Task NewSession_WhenTheDialogIsCancelled_AddsNoSession()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns((NewSessionResult?)null);
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions.Should().BeEmpty();
        vm.HasSessions.Should().BeFalse();
    }

    [Fact]
    public async Task NewSession_AddsASessionSelectsItAndFlipsHasSessions()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions.Should().ContainSingle();
        vm.HasSessions.Should().BeTrue();
        vm.SelectedSession.Should().Be(vm.Sessions[0]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
        vm.SelectedSession.Title.Should().Be("default - 1");
    }

    [Fact]
    public async Task ShowTimestamps_TogglesEveryOpenSessionLive()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.ShowTimestamps = true;

        vm.Sessions.Should().OnlyContain(s => s.ShowTimestamps);
    }

    [Fact]
    public async Task ShowTimestamps_WhenOn_AppliesToASessionCreatedAfterwards()
    {
        var vm = NewVm();
        vm.ShowTimestamps = true;

        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions.Single().ShowTimestamps.Should().BeTrue();
    }

    [Fact]
    public async Task AutoCloseOnExit_TogglesEveryOpenSessionLive()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.AutoCloseOnExit = true;

        vm.Sessions.Should().OnlyContain(s => s.AutoCloseOnExit);
    }

    [Fact]
    public void ShowSinglePane_IsTrueWhenEitherZoomedOrSingleLayout()
    {
        var vm = NewVm();
        vm.ShowSinglePane.Should().BeFalse();

        vm.IsZoomed = true;
        vm.ShowSinglePane.Should().BeTrue();

        vm.IsZoomed = false;
        vm.SingleSessionLayout = true;
        vm.ShowSinglePane.Should().BeTrue();
    }

    [Fact]
    public async Task SessionCloseRequested_ClosesThatSessionThroughTheCockpit()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        session.RequestSelfClose();

        vm.Sessions.Should().NotContain(session);
    }

    [Fact]
    public async Task NewSession_WithADialogName_UsesItAsTheSessionTitle()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(new NewSessionResult(
            SessionKind.Sdk,
            new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort,
            "My debug session"));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions[0].Title.Should().Be("My debug session");
    }

    [Fact]
    public async Task NewSession_AssignsIncrementingTitles()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions[0].Title.Should().Be("default - 1");
        vm.Sessions[1].Title.Should().Be("default - 2");
    }

    [Fact]
    public async Task SelectSession_SwitchesSelectionAndIsSelectedFlags()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];

        vm.SelectSessionCommand.Execute(second);

        vm.SelectedSession.Should().Be(second);
        first.IsSelected.Should().BeFalse();
        second.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task CloseSession_RemovesItFromSessions()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];

        await vm.CloseSessionCommand.ExecuteAsync(session);

        vm.Sessions.Should().NotContain(session);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheSelectedSession_SelectsAnotherRemainingSession()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];
        vm.SelectSessionCommand.Execute(first);

        await vm.CloseSessionCommand.ExecuteAsync(first);

        vm.SelectedSession.Should().Be(second);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheLastSession_ClearsSelectionZoomAndHasSessions()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        vm.ToggleZoomCommand.Execute(null);

        await vm.CloseSessionCommand.ExecuteAsync(session);

        vm.SelectedSession.Should().BeNull();
        vm.IsZoomed.Should().BeFalse();
        vm.HasSessions.Should().BeFalse();
    }

    [Fact]
    public async Task RequestCloseSession_WhenTheSessionIsIdle_ClosesItImmediately()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Idle;

        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        vm.Sessions.Should().NotContain(session);
        session.IsConfirmingClose.Should().BeFalse();
    }

    [Fact]
    public async Task RequestCloseSession_WhenTheSessionIsBusy_AsksForConfirmationAndKeepsTheSession()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;

        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        vm.Sessions.Should().Contain(session);
        session.IsConfirmingClose.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmCloseSession_ClosesTheSessionAndClearsTheConfirmFlag()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;
        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        await vm.ConfirmCloseSessionCommand.ExecuteAsync(session);

        vm.Sessions.Should().NotContain(session);
        session.IsConfirmingClose.Should().BeFalse();
    }

    [Fact]
    public async Task CancelCloseSession_KeepsTheSessionAndClearsTheConfirmFlag()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        var session = vm.Sessions[0];
        session.SessionStatus = SessionStatus.Busy;
        await vm.RequestCloseSessionCommand.ExecuteAsync(session);

        vm.CancelCloseSessionCommand.Execute(session);

        vm.Sessions.Should().Contain(session);
        session.IsConfirmingClose.Should().BeFalse();
    }

    [Fact]
    public async Task NewSession_WhenTheDialogPicksTty_AddsATtyPanelAndSelectsIt()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions.Should().ContainSingle();
        vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>();
        vm.SelectedSession.Should().Be(vm.Sessions[0]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task NewSession_MixingSdkAndTtyPicks_ContinuesTheSharedTitleCounter()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(
            NewSessionResultFor(SessionKind.Sdk),
            NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);

        vm.Sessions[0].Title.Should().Be("default - 1");
        vm.Sessions[1].Title.Should().Be("default - 2");
    }

    [Fact]
    public void ToggleZoom_FlipsIsZoomed()
    {
        var vm = NewVm();

        vm.ToggleZoomCommand.Execute(null);
        vm.IsZoomed.Should().BeTrue();

        vm.ToggleZoomCommand.Execute(null);
        vm.IsZoomed.Should().BeFalse();
    }

    [Fact]
    public async Task SelectNextSession_MovesToTheFollowingSession()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        vm.SelectedSession.Should().Be(vm.Sessions[1]);
    }

    [Fact]
    public async Task SelectNextSession_FromTheLastSession_WrapsToTheFirst()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectNextSession();

        vm.SelectedSession.Should().Be(vm.Sessions[0]);
    }

    [Fact]
    public async Task SelectPreviousSession_MovesToThePrecedingSession()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(vm.Sessions[1]);
    }

    [Fact]
    public async Task SelectPreviousSession_FromTheFirstSession_WrapsToTheLast()
    {
        var vm = await NewVmWithSessionsAsync(3);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(vm.Sessions[2]);
    }

    [Fact]
    public async Task SelectNextSession_KeepsIsSelectedFlagsConsistent()
    {
        var vm = await NewVmWithSessionsAsync(2);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        vm.Sessions[0].IsSelected.Should().BeFalse();
        vm.Sessions[1].IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task SelectNextSession_WithASingleSession_StaysOnThatSession()
    {
        var vm = await NewVmWithSessionsAsync(1);
        var only = vm.Sessions[0];

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(only);
        only.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectNextSession_WithNoSessions_DoesNothing()
    {
        var vm = NewVm();

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        vm.SelectedSession.Should().BeNull();
        vm.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task GridColumns_IsOneForZeroOrOneSessionAndTwoForMore()
    {
        var vm = NewVm();
        vm.GridColumns.Should().Be(1);

        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.GridColumns.Should().Be(1);

        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.GridColumns.Should().Be(2);

        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.GridColumns.Should().Be(2);
    }

    [Fact]
    public async Task SaveAllSettingsCommand_PersistsEverySectionAndReportsEachAsSaved()
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

        var vm = new CockpitViewModel(
            () => new SessionViewModel(),
            () => new ClaudeTtyViewModel(),
            DefaultDialogService(),
            Substitute.For<IAudioCaptureService>(),
            Substitute.For<IAudioPlaybackService>(),
            Substitute.For<IAttentionNotifier>(),
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore);

        await vm.SaveAllSettingsCommand.ExecuteAsync(null);

        await notificationSettingsStore.Received(1).SaveAsync(Arg.Any<NotificationSettings>(), Arg.Any<CancellationToken>());
        await transcriptDisplaySettingsStore.Received(1).SaveAsync(Arg.Any<TranscriptDisplaySettings>(), Arg.Any<CancellationToken>());
        await sessionBehaviorSettingsStore.Received(1).SaveAsync(Arg.Any<SessionBehaviorSettings>(), Arg.Any<CancellationToken>());
        await layoutSettingsStore.Received(1).SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>());
        await voiceSettingsStore.Received(1).SaveAsync(Arg.Any<VoiceSettings>(), Arg.Any<CancellationToken>());
        await terminalSettingsStore.Received(1).SaveAsync(Arg.Any<TerminalSettings>(), Arg.Any<CancellationToken>());

        vm.NotificationSettingsStatus.Should().Be("✓ Saved");
        vm.TranscriptDisplaySettingsStatus.Should().Be("✓ Saved");
        vm.SessionBehaviorSettingsStatus.Should().Be("✓ Saved");
        vm.LayoutSettingsStatus.Should().Be("✓ Saved");
        vm.VoiceSettingsStatus.Should().Be("✓ Saved");
        vm.TerminalSettingsStatus.Should().Be("✓ Saved");
    }

    [Fact]
    public void Constructor_DefaultsSidebarWidthBeforeLayoutSettingsLoad()
    {
        var vm = NewVm();

        vm.SidebarWidth.Should().Be(LayoutSettings.DefaultSidebarWidth);
    }

    [Fact]
    public async Task Constructor_LoadsSidebarWidthFromLayoutSettingsStore()
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings { SidebarWidth = 300 });

        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);
        await Task.Delay(50);

        vm.SidebarWidth.Should().Be(300);
    }

    [Fact]
    public async Task SetSidebarWidthAsync_PersistsTheWidthAndUpdatesTheProperty()
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);

        await vm.SetSidebarWidthAsync(320);

        vm.SidebarWidth.Should().Be(320);
        await layoutSettingsStore.Received(1).SaveAsync(
            Arg.Is<LayoutSettings>(s => s.SidebarWidth == 320), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(40, LayoutSettings.MinSidebarWidth)]
    [InlineData(2000, LayoutSettings.MaxSidebarWidth)]
    public async Task SetSidebarWidthAsync_ClampsAnOutOfRangeWidth(double requested, double expected)
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        var vm = NewVm(layoutSettingsStore: layoutSettingsStore);

        await vm.SetSidebarWidthAsync(requested);

        vm.SidebarWidth.Should().Be(expected);
        await layoutSettingsStore.Received(1).SaveAsync(
            Arg.Is<LayoutSettings>(s => s.SidebarWidth == expected), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Constructor_LoadsTerminalSettingsFromStore()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 18 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);

        // The load runs fire-and-forget from the constructor (same pattern as the other settings
        // sections); give it a beat to complete before asserting.
        await Task.Delay(50);

        vm.TerminalFontFamily.Should().Be("JetBrains Mono");
        vm.TerminalFontSize.Should().Be(18);
    }

    [Fact]
    public async Task SaveTerminalSettingsCommand_ClampsFontSizeAndTrimsBlankFontFamilyToTheDefault()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        vm.TerminalFontFamily = "   ";
        vm.TerminalFontSize = 999;

        await vm.SaveTerminalSettingsCommand.ExecuteAsync(null);

        await terminalSettingsStore.Received(1).SaveAsync(
            Arg.Is<TerminalSettings>(s => s.FontFamily == "Cascadia Mono, Consolas, monospace" && s.FontSize == TerminalSettings.MaxFontSize),
            Arg.Any<CancellationToken>());
        vm.TerminalFontFamily.Should().Be("Cascadia Mono, Consolas, monospace");
        vm.TerminalFontSize.Should().Be(TerminalSettings.MaxFontSize);
        vm.TerminalSettingsStatus.Should().Be("✓ Saved");
    }

    [Fact]
    public async Task NewTtySession_IsSeededWithTheCurrentTerminalFontSettings()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        vm.TerminalFontFamily = "Fira Code";
        vm.TerminalFontSize = 20;

        await vm.NewSessionCommand.ExecuteAsync(null);

        var tty = vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>().Subject;
        tty.TerminalFontFamily.Should().Be("Fira Code");
        tty.TerminalFontSize.Should().Be(20);
    }

    [Fact]
    public async Task ChangingTerminalFontSettings_PushesLiveToOpenTtySessions()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var tty = vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>().Subject;

        vm.TerminalFontFamily = "DejaVu Sans Mono";
        vm.TerminalFontSize = 24;

        tty.TerminalFontFamily.Should().Be("DejaVu Sans Mono");
        tty.TerminalFontSize.Should().Be(24);
    }

    [Fact]
    public async Task LoadingACuratedFont_SelectsItInTheDropdownWithoutCustomMode()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "JetBrains Mono", FontSize = 14 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        await Task.Delay(50);

        vm.TerminalFontSelection.Should().Be("JetBrains Mono");
        vm.IsTerminalFontCustom.Should().BeFalse();
    }

    [Fact]
    public async Task LoadingAFontOutsideTheCuratedList_ReopensInCustomMode()
    {
        var terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
        terminalSettingsStore.LoadAsync().Returns(new TerminalSettings { FontFamily = "Comic Mono", FontSize = 14 });

        var vm = NewVm(terminalSettingsStore: terminalSettingsStore);
        await Task.Delay(50);

        vm.TerminalFontSelection.Should().Be(CockpitViewModel.CustomFontChoice);
        vm.IsTerminalFontCustom.Should().BeTrue();
        vm.TerminalCustomFontFamily.Should().Be("Comic Mono");
        vm.TerminalFontFamily.Should().Be("Comic Mono");
    }

    [Fact]
    public async Task NewTtySession_IsSeededWithTheCurrentVerticalLayoutSetting()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        vm.StackSessionsVertically = true;

        await vm.NewSessionCommand.ExecuteAsync(null);

        var tty = vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>().Subject;
        tty.IsVerticalLayout.Should().BeTrue();
    }

    [Fact]
    public async Task ChangingStackSessionsVertically_PushesLiveToOpenTtySessions()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Tty));
        var vm = NewVm(dialogService);
        await vm.NewSessionCommand.ExecuteAsync(null);
        var tty = vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>().Subject;

        vm.StackSessionsVertically = true;

        tty.IsVerticalLayout.Should().BeTrue();
    }

    [Fact]
    public void ChoosingCustomThenTypingAFont_DrivesTheEffectiveFontFamily()
    {
        var vm = NewVm();

        vm.TerminalFontSelection = CockpitViewModel.CustomFontChoice;
        vm.IsTerminalFontCustom.Should().BeTrue();

        vm.TerminalCustomFontFamily = "Comic Mono, monospace";

        vm.TerminalFontFamily.Should().Be("Comic Mono, monospace");
    }

    [Fact]
    public void SwitchingFromCustomBackToACuratedFont_UsesThatFontAndLeavesCustomMode()
    {
        var vm = NewVm();
        vm.TerminalFontSelection = CockpitViewModel.CustomFontChoice;
        vm.TerminalCustomFontFamily = "Comic Mono";

        vm.TerminalFontSelection = "Consolas";

        vm.IsTerminalFontCustom.Should().BeFalse();
        vm.TerminalFontFamily.Should().Be("Consolas");
    }

    // #52: a settings-Save should let a plugin's already-built contributions (e.g. a side-menu section that
    // fetched data once at construction) refresh without an app restart. CockpitViewModel is the
    // IPluginContributionSink every CockpitHost is built against, so it's the seam that routes a save for one
    // plugin's id to only that plugin's registered callbacks.
    [Fact]
    public void SettingsSaved_RunsOnlyTheHandlersRegisteredForThatPluginId()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;
        var prCalls = 0;
        var youTrackCalls = 0;
        sink.AddSettingsSavedHandler("github-pull-requests", () => prCalls++);
        sink.AddSettingsSavedHandler("youtrack", () => youTrackCalls++);

        sink.NotifySettingsSaved("github-pull-requests");

        prCalls.Should().Be(1);
        youTrackCalls.Should().Be(0);
    }

    [Fact]
    public void SettingsSaved_RunsEveryHandlerRegisteredForThatPluginId()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;
        var firstCalls = 0;
        var secondCalls = 0;
        sink.AddSettingsSavedHandler("youtrack", () => firstCalls++);
        sink.AddSettingsSavedHandler("youtrack", () => secondCalls++);

        sink.NotifySettingsSaved("youtrack");

        firstCalls.Should().Be(1);
        secondCalls.Should().Be(1);
    }

    [Fact]
    public void SettingsSaved_WithNoHandlersRegistered_DoesNotThrow()
    {
        var vm = NewVm();
        var sink = (IPluginContributionSink)vm;

        var act = () => sink.NotifySettingsSaved("no-such-plugin");

        act.Should().NotThrow();
    }

    // Settings are now reachable from several places (the manager's gear, the gear on a plugin's left-menu entry
    // or dialog, and the plugin itself), and every one of them opens the same dialog through this one seam —
    // titled after the plugin, whichever gear was pressed.
    [Fact]
    public async Task OpenPluginSettings_OpensThePluginsOwnViewTitledAfterIt()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var vm = NewVm(pluginDialogHost: dialogHost);
        var view = new TextBlock();
        ((IPluginContributionSink)vm).AddPluginSettings("youtrack", "YouTrack", () => view);

        await vm.OpenPluginSettingsAsync("youtrack");

        await dialogHost.Received(1).ShowSettingsDialogAsync(
            "YouTrack settings",
            Arg.Any<Func<Control>>(),
            Arg.Any<double>(),
            Arg.Any<double>(),
            Arg.Any<Action?>());
    }

    // Saving from any gear must run the plugin's settings-saved handlers: a plugin that re-registers its MCP
    // server on save cannot depend on which one the operator reached for.
    [Fact]
    public async Task SavingFromAnyGear_RunsThePluginsSettingsSavedHandlers()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        dialogHost
            .ShowSettingsDialogAsync(Arg.Any<string>(), Arg.Any<Func<Control>>(), Arg.Any<double>(), Arg.Any<double>(), Arg.Any<Action?>())
            .Returns(callInfo =>
            {
                callInfo.Arg<Action?>()?.Invoke();
                return Task.CompletedTask;
            });
        var vm = NewVm(pluginDialogHost: dialogHost);
        var sink = (IPluginContributionSink)vm;
        var saves = 0;
        sink.AddPluginSettings("youtrack", "YouTrack", () => new TextBlock());
        sink.AddSettingsSavedHandler("youtrack", () => saves++);

        await vm.OpenPluginSettingsAsync("youtrack");

        saves.Should().Be(1);
    }

    [Fact]
    public async Task OpenPluginSettings_ForAPluginThatRegisteredNone_DoesNothing()
    {
        var dialogHost = Substitute.For<IPluginDialogHost>();
        var vm = NewVm(pluginDialogHost: dialogHost);

        await vm.OpenPluginSettingsAsync("youtrack");

        vm.HasPluginSettings("youtrack").Should().BeFalse();
        await dialogHost.DidNotReceiveWithAnyArgs().ShowSettingsDialogAsync(default!, default!, default, default, default);
    }

    private static async Task<CockpitViewModel> NewVmWithSessionsAsync(int count)
    {
        var vm = NewVm();
        for (var i = 0; i < count; i++)
        {
            await vm.NewSessionCommand.ExecuteAsync(null);
        }

        return vm;
    }

    [Fact]
    public async Task OpenPluginStoreUpdatesAsync_OpensTheStoreDialogWithTheAvailableUpdatesFilterPreselected()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        var vm = NewVm(dialogService);

        await vm.OpenPluginStoreUpdatesAsync();

        await dialogService.Received(1).ShowPluginStoreDialogAsync(
            Arg.Is<PluginManagerViewModel>(manager => manager == vm.Plugins),
            PluginStoreFilter.UpdatesAvailable);
    }

    [Fact]
    public void ActiveShortcuts_KeepTheSessionManagementActionsLiveOverTheTerminal_ButNotTheDialogOpeners()
    {
        var vm = NewVm();

        var previousSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Previous session");
        var nextSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Next session");
        var newSession = vm.ActiveShortcuts.Single(binding => binding.Label == "New session");
        var duplicateSession = vm.ActiveShortcuts.Single(binding => binding.Label == "Duplicate active session");
        var manageProfiles = vm.ActiveShortcuts.Single(binding => binding.Label == "Manage profiles");

        previousSession.Gesture.Should().Be("Ctrl+Up");
        previousSession.ActiveInTerminal.Should().BeTrue();
        nextSession.Gesture.Should().Be("Ctrl+Down");
        nextSession.ActiveInTerminal.Should().BeTrue();

        // Session-management actions fire over a focused terminal (Raymond's call).
        newSession.ActiveInTerminal.Should().BeTrue();
        duplicateSession.ActiveInTerminal.Should().BeTrue();

        // A dialog-opener on a single-key shell gesture (Ctrl+R) stays gated so it reaches the shell.
        manageProfiles.ActiveInTerminal.Should().BeFalse();
    }

    [Fact]
    public async Task SelectNextSessionCommand_MovesTheSelectionAndWraps()
    {
        var vm = NewVm();
        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewSessionCommand.ExecuteAsync(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSessionCommand.Execute(null);
        vm.SelectedSession.Should().Be(vm.Sessions[1]);

        vm.SelectNextSessionCommand.Execute(null);
        vm.SelectedSession.Should().Be(vm.Sessions[0]);

        vm.SelectPreviousSessionCommand.Execute(null);
        vm.SelectedSession.Should().Be(vm.Sessions[1]);
    }

    private static CockpitViewModel NewVm(
        ISessionDialogService? dialogService = null,
        ITerminalSettingsStore? terminalSettingsStore = null,
        ILayoutSettingsStore? layoutSettingsStore = null,
        IPluginDialogHost? pluginDialogHost = null)
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var transcriptDisplaySettingsStore = Substitute.For<ITranscriptDisplaySettingsStore>();
        transcriptDisplaySettingsStore.LoadAsync().Returns(new TranscriptDisplaySettings());
        var sessionBehaviorSettingsStore = Substitute.For<ISessionBehaviorSettingsStore>();
        sessionBehaviorSettingsStore.LoadAsync().Returns(new SessionBehaviorSettings());
        if (layoutSettingsStore is null)
        {
            layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
            layoutSettingsStore.LoadAsync().Returns(new LayoutSettings());
        }

        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync().Returns(new VoiceSettings());
        if (terminalSettingsStore is null)
        {
            terminalSettingsStore = Substitute.For<ITerminalSettingsStore>();
            terminalSettingsStore.LoadAsync().Returns(new TerminalSettings());
        }

        return new CockpitViewModel(
            () => new SessionViewModel(),
            () => new ClaudeTtyViewModel(),
            dialogService ?? DefaultDialogService(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            transcriptDisplaySettingsStore,
            sessionBehaviorSettingsStore,
            layoutSettingsStore,
            voiceSettingsStore,
            terminalSettingsStore,
            pluginDialogHost: pluginDialogHost);
    }

    private static ISessionDialogService DefaultDialogService()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync().Returns(NewSessionResultFor(SessionKind.Sdk));
        return dialogService;
    }

    private static NewSessionResult NewSessionResultFor(SessionKind kind) => new(
        kind,
        new SessionProfile("default", new ClaudeConfig(@"C:\fake\.claude")),
        SessionOptionCatalog.DefaultPermissionMode,
        SessionOptionCatalog.DefaultModel,
        SessionOptionCatalog.DefaultEffort, null);
}
