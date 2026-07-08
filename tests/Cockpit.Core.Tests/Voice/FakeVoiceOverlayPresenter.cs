using Cockpit.App.Services;

namespace Cockpit.Core.Tests.Voice;

/// <summary>Test double for <see cref="IVoiceOverlayPresenter"/> — records Show/Hide calls without touching a real Avalonia window.</summary>
internal sealed class FakeVoiceOverlayPresenter : IVoiceOverlayPresenter
{
    public int ShowCallCount { get; private set; }

    public int HideCallCount { get; private set; }

    public void Show() => ShowCallCount++;

    public void Hide() => HideCallCount++;
}
