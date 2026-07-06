using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Notifications;
using Cockpit.Core.Profiles;
using Cockpit.Core.SessionSwitching;
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
    public async Task NewSession_WhenTheDialogIsCancelled_AddsNoSession()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<SessionKind>()).Returns((NewSessionResult?)null);
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
        vm.SelectedSession.Title.Should().Be("Claude 1");
    }

    [Fact]
    public async Task NewSession_WithADialogName_UsesItAsTheSessionTitle()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<SessionKind>()).Returns(new NewSessionResult(
            new ClaudeProfile("default", @"C:\fake\.claude"),
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

        vm.Sessions[0].Title.Should().Be("Claude 1");
        vm.Sessions[1].Title.Should().Be("Claude 2");
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
    public async Task NewTtySession_AddsATtyPanelAndSelectsIt()
    {
        var vm = NewVm();

        await vm.NewTtySessionCommand.ExecuteAsync(null);

        vm.Sessions.Should().ContainSingle();
        vm.Sessions[0].Should().BeOfType<ClaudeTtyViewModel>();
        vm.SelectedSession.Should().Be(vm.Sessions[0]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task NewTtySession_ContinuesTheSharedTitleCounter()
    {
        var vm = NewVm();

        await vm.NewSessionCommand.ExecuteAsync(null);
        await vm.NewTtySessionCommand.ExecuteAsync(null);

        vm.Sessions[0].Title.Should().Be("Claude 1");
        vm.Sessions[1].Title.Should().Be("Claude 2");
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
    public void CurrentSessionSwitchSettings_ReflectsTheLiveEnableAndModifierEdits()
    {
        var vm = NewVm();

        vm.SessionSwitchEnabled = false;
        vm.SelectedSessionSwitchModifier =
            vm.SessionSwitchModifiers.Single(option => option.Value == SessionSwitchModifier.CtrlAlt);

        vm.CurrentSessionSwitchSettings.IsEnabled.Should().BeFalse();
        vm.CurrentSessionSwitchSettings.Modifier.Should().Be(SessionSwitchModifier.CtrlAlt);
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

    private static CockpitViewModel NewVm(ISessionDialogService? dialogService = null)
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        var attentionNotifier = Substitute.For<IAttentionNotifier>();
        var notificationSettingsStore = Substitute.For<INotificationSettingsStore>();
        notificationSettingsStore.LoadAsync().Returns(new NotificationSettings());
        var sessionSwitchSettingsStore = Substitute.For<ISessionSwitchSettingsStore>();
        sessionSwitchSettingsStore.LoadAsync().Returns(new SessionSwitchSettings());
        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            dialogService ?? DefaultDialogService(),
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            sessionSwitchSettingsStore);
    }

    private static ISessionDialogService DefaultDialogService()
    {
        var dialogService = Substitute.For<ISessionDialogService>();
        dialogService.ShowNewSessionDialogAsync(Arg.Any<SessionKind>()).Returns(new NewSessionResult(
            new ClaudeProfile("default", @"C:\fake\.claude"),
            SessionOptionCatalog.DefaultPermissionMode,
            SessionOptionCatalog.DefaultModel,
            SessionOptionCatalog.DefaultEffort, null));
        return dialogService;
    }
}
