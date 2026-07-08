namespace Cockpit.App.Services;

/// <summary>
/// Shows/hides the floating voice-overlay pill. A seam over the real
/// <see cref="Cockpit.App.Views.VoiceOverlayWindow"/> so <see cref="VoicePushToTalkCoordinator"/>'s
/// routing logic is unit-testable without a running Avalonia UI thread — the same reason native
/// dependencies elsewhere in the cockpit (audio capture, speech-to-text) sit behind an interface.
/// </summary>
public interface IVoiceOverlayPresenter
{
    /// <summary>Positions the pill bottom-centre and shows it.</summary>
    void Show();

    /// <summary>Hides the pill without destroying the window (it is reused for the next hold).</summary>
    void Hide();
}
