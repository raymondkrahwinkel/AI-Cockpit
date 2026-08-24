using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

// Real `IVoiceOverlayPresenter`: lazily creates the single shared `VoiceOverlayWindow` on first show, since
// Avalonia must be fully initialized before a Window can be constructed — true by the time
// `VoicePushToTalkCoordinator.StartAsync` runs — and reuses it for every subsequent hold.
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
