using CommunityToolkit.Mvvm.ComponentModel;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.ViewModels;

/// <summary>
/// Drives the floating voice-input pill (#34): one shared instance for the whole cockpit (single-user,
/// one hold at a time), whose <see cref="State"/> the pill's XAML binds its two rows'
/// (Listening/Transcribing) visibility to. <c>VoicePushToTalkCoordinator</c> owns the transitions.
/// </summary>
public partial class VoiceOverlayViewModel : ViewModelBase, ISingletonService
{
    [ObservableProperty]
    private VoiceOverlayState _state = VoiceOverlayState.Hidden;

    public bool IsListening => State == VoiceOverlayState.Listening;

    public bool IsTranscribing => State == VoiceOverlayState.Transcribing;

    partial void OnStateChanged(VoiceOverlayState value)
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsTranscribing));
    }
}
