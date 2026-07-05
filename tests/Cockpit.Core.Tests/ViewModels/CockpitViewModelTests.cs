using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Abstractions.Notifications;
using Cockpit.Core.Abstractions.SessionSwitching;
using Cockpit.Core.Notifications;
using Cockpit.Core.SessionSwitching;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Exercises <see cref="CockpitViewModel"/>'s session-manager surface (new/select/close) against a
/// fake session factory — no real <c>IClaudeSession</c>/CLI process involved.
/// </summary>
public class CockpitViewModelTests
{
    [Fact]
    public void Constructor_StartsWithOneSessionSelected()
    {
        var vm = NewVm();

        vm.Sessions.Should().ContainSingle();
        vm.SelectedSession.Should().Be(vm.Sessions[0]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
        vm.SelectedSession.Title.Should().Be("Claude 1");
    }

    [Fact]
    public void NewSession_AddsAFurtherSessionAndSelectsIt()
    {
        var vm = NewVm();

        vm.NewSessionCommand.Execute(null);

        vm.Sessions.Should().HaveCount(2);
        vm.SelectedSession.Should().Be(vm.Sessions[1]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void NewSession_AssignsIncrementingTitles()
    {
        var vm = NewVm();

        vm.NewSessionCommand.Execute(null);

        vm.Sessions[0].Title.Should().Be("Claude 1");
        vm.Sessions[1].Title.Should().Be("Claude 2");
    }

    [Fact]
    public void SelectSession_SwitchesSelectionAndIsSelectedFlags()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
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
        var session = vm.Sessions[0];

        await vm.CloseSessionCommand.ExecuteAsync(session);

        vm.Sessions.Should().NotContain(session);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheSelectedSession_SelectsAnotherRemainingSession()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        var first = vm.Sessions[0];
        var second = vm.Sessions[1];
        vm.SelectSessionCommand.Execute(first);

        await vm.CloseSessionCommand.ExecuteAsync(first);

        vm.SelectedSession.Should().Be(second);
    }

    [Fact]
    public async Task CloseSession_WhenClosingTheLastSession_ClearsSelectionAndZoom()
    {
        var vm = NewVm();
        var session = vm.Sessions[0];
        vm.ToggleZoomCommand.Execute(null);

        await vm.CloseSessionCommand.ExecuteAsync(session);

        vm.SelectedSession.Should().BeNull();
        vm.IsZoomed.Should().BeFalse();
    }

    [Fact]
    public void NewTtySession_AddsATtyPanelAndSelectsIt()
    {
        var vm = NewVm();

        vm.NewTtySessionCommand.Execute(null);

        vm.Sessions.Should().HaveCount(2);
        vm.Sessions[1].Should().BeOfType<ClaudeTtyViewModel>();
        vm.SelectedSession.Should().Be(vm.Sessions[1]);
        vm.SelectedSession!.IsSelected.Should().BeTrue();
    }

    [Fact]
    public void NewTtySession_ContinuesTheSharedTitleCounter()
    {
        var vm = NewVm();

        vm.NewTtySessionCommand.Execute(null);

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
    public void SelectNextSession_MovesToTheFollowingSession()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        vm.NewSessionCommand.Execute(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        vm.SelectedSession.Should().Be(vm.Sessions[1]);
    }

    [Fact]
    public void SelectNextSession_FromTheLastSession_WrapsToTheFirst()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        vm.NewSessionCommand.Execute(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectNextSession();

        vm.SelectedSession.Should().Be(vm.Sessions[0]);
    }

    [Fact]
    public void SelectPreviousSession_MovesToThePrecedingSession()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        vm.NewSessionCommand.Execute(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[2]);

        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(vm.Sessions[1]);
    }

    [Fact]
    public void SelectPreviousSession_FromTheFirstSession_WrapsToTheLast()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        vm.NewSessionCommand.Execute(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(vm.Sessions[2]);
    }

    [Fact]
    public void SelectNextSession_KeepsIsSelectedFlagsConsistent()
    {
        var vm = NewVm();
        vm.NewSessionCommand.Execute(null);
        vm.SelectSessionCommand.Execute(vm.Sessions[0]);

        vm.SelectNextSession();

        vm.Sessions[0].IsSelected.Should().BeFalse();
        vm.Sessions[1].IsSelected.Should().BeTrue();
    }

    [Fact]
    public void SelectNextSession_WithASingleSession_StaysOnThatSession()
    {
        var vm = NewVm();
        var only = vm.Sessions[0];

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        vm.SelectedSession.Should().Be(only);
        only.IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task SelectNextSession_WithNoSessions_DoesNothing()
    {
        var vm = NewVm();
        await vm.CloseSessionCommand.ExecuteAsync(vm.Sessions[0]);

        vm.SelectNextSession();
        vm.SelectPreviousSession();

        vm.SelectedSession.Should().BeNull();
        vm.Sessions.Should().BeEmpty();
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

    private static CockpitViewModel NewVm()
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
            captureService,
            playbackService,
            attentionNotifier,
            notificationSettingsStore,
            sessionSwitchSettingsStore);
    }
}
