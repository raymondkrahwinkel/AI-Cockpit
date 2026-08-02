using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// Real `IVoiceOverlayPresenter`: lazily creates the single shared
// `VoiceOverlayWindow` on first show (Avalonia must be fully initialized before a Window
// can be constructed, which it is by the time `VoicePushToTalkCoordinator.StartAsync` runs from
// `App.axaml.cs`) and reuses it for every subsequent hold.
internal sealed class VoiceOverlayPresenter(VoiceOverlayViewModel overlay) : IVoiceOverlayPresenter, ISingletonService
{
    private VoiceOverlayWindow? _window;

    public void Show()
    {
        var window = _window ??= new VoiceOverlayWindow { DataContext = overlay };
        window.PositionBottomCenter();
        window.Show();
    }

    public void Hide() => _window?.Hide();
}
