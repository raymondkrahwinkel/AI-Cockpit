using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Audio;
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

    private static CockpitViewModel NewVm()
    {
        var captureService = Substitute.For<IAudioCaptureService>();
        var playbackService = Substitute.For<IAudioPlaybackService>();
        return new CockpitViewModel(
            () => new ClaudeSessionViewModel(),
            () => new ClaudeTtyViewModel(),
            captureService,
            playbackService);
    }
}
