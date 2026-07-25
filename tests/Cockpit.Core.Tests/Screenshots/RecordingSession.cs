using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Screenshots;

/// <summary>
/// A minimal session that records what was injected into it and answers with whatever reason the test wants.
/// Stands in for a real session kind where the point under test is the routing, not what a chat panel does with
/// an image — that needs a decoded bitmap and so an Avalonia platform, which is a different test project.
/// </summary>
internal sealed class RecordingSession : SessionPanelViewModel
{
    /// <summary>What this session answers a screenshot with: null for "it landed", a sentence for "it could not".</summary>
    public string? RefusalReason { get; init; }

    public List<byte[]> InjectedScreenshots { get; } = [];

    protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng)
    {
        if (RefusalReason is not null)
        {
            return Task.FromResult<string?>(RefusalReason);
        }

        InjectedScreenshots.Add(screenshotPng);
        return Task.FromResult<string?>(null);
    }

    protected override void OnVoiceTextReady(string text)
    {
    }

    public override Task<bool> FeedVerifyResultAsync(string caption, byte[] screenshotPng) => Task.FromResult(false);

    protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;
}
