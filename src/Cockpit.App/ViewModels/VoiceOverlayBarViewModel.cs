using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

/// <summary>One bar of the voice overlay's live microphone waveform — its <see cref="Height"/> tracks the captured level for that slot in the scrolling history (#34b).</summary>
public partial class VoiceOverlayBarViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _height;
}
