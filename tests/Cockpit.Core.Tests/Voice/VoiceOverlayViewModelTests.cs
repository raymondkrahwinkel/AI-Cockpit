using Cockpit.App.ViewModels;
using FluentAssertions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>The voice-overlay pill's derived visibility flags (#34) — the XAML binds each state's row to exactly one of these.</summary>
public class VoiceOverlayViewModelTests
{
    [Fact]
    public void InitialState_IsHidden_WithBothRowsHidden()
    {
        var vm = new VoiceOverlayViewModel();

        vm.State.Should().Be(VoiceOverlayState.Hidden);
        vm.IsListening.Should().BeFalse();
        vm.IsTranscribing.Should().BeFalse();
    }

    [Fact]
    public void State_Listening_OnlyIsListeningIsTrue()
    {
        var vm = new VoiceOverlayViewModel { State = VoiceOverlayState.Listening };

        vm.IsListening.Should().BeTrue();
        vm.IsTranscribing.Should().BeFalse();
    }

    [Fact]
    public void State_Transcribing_OnlyIsTranscribingIsTrue()
    {
        var vm = new VoiceOverlayViewModel { State = VoiceOverlayState.Transcribing };

        vm.IsListening.Should().BeFalse();
        vm.IsTranscribing.Should().BeTrue();
    }

    [Fact]
    public void State_BackToHidden_BothRowsHiddenAgain()
    {
        var vm = new VoiceOverlayViewModel { State = VoiceOverlayState.Listening };

        vm.State = VoiceOverlayState.Hidden;

        vm.IsListening.Should().BeFalse();
        vm.IsTranscribing.Should().BeFalse();
    }

    [Fact]
    public void NewOverlay_HasAFullSetOfFlatWaveformBars()
    {
        var vm = new VoiceOverlayViewModel();

        vm.Bars.Should().NotBeEmpty();
        vm.Bars.Should().OnlyContain(bar => bar.Height == 2);
    }

    [Fact]
    public void PushLevel_RaisesTheNewestBar_AndLeavesOlderBarsAtRest()
    {
        var vm = new VoiceOverlayViewModel();

        vm.PushLevel(1.0);

        vm.Bars[^1].Height.Should().Be(20);
        vm.Bars[0].Height.Should().Be(2);
    }

    [Fact]
    public void PushLevel_ScrollsLevelsAcrossTheBars()
    {
        var vm = new VoiceOverlayViewModel();

        vm.PushLevel(1.0);
        vm.PushLevel(0.0);

        vm.Bars[^1].Height.Should().Be(2);
        vm.Bars[^2].Height.Should().Be(20);
    }

    [Fact]
    public void LeavingListening_FlattensTheWaveform()
    {
        var vm = new VoiceOverlayViewModel { State = VoiceOverlayState.Listening };
        vm.PushLevel(1.0);

        vm.State = VoiceOverlayState.Transcribing;

        vm.Bars.Should().OnlyContain(bar => bar.Height == 2);
    }
}
