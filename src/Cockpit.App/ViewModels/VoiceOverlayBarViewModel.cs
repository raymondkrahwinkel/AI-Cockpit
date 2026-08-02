using CommunityToolkit.Mvvm.ComponentModel;

namespace Cockpit.App.ViewModels;

// One bar of the voice overlay's live microphone waveform — its `Height` tracks the captured level for that slot in the scrolling history (#34b).
public partial class VoiceOverlayBarViewModel : ViewModelBase
{
    [ObservableProperty]
    private double _height;
}
