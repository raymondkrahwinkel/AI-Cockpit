using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions;

namespace Cockpit.App.Services;

/// <summary>
/// Real <see cref="IVoiceOverlayPresenter"/>: lazily creates the single shared
/// <see cref="VoiceOverlayWindow"/> on first show (Avalonia must be fully initialized before a Window
/// can be constructed, which it is by the time <c>VoicePushToTalkCoordinator.StartAsync</c> runs from
/// <c>App.axaml.cs</c>) and reuses it for every subsequent hold.
/// </summary>
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
