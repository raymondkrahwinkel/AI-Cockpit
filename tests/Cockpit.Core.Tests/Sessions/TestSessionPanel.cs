using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// A session panel that records what was sent into it, so a scheduled resume can be tested without a runtime or
/// a pty behind it. The rest of the panel contract is inert here — this stands in for a session, it is not one.
/// </summary>
internal sealed class TestSessionPanel : SessionPanelViewModel
{
    public List<string> Sent { get; } = [];

    public override Task<bool> SendPromptAsync(string prompt)
    {
        Sent.Add(prompt);
        return Task.FromResult(true);
    }

    protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    protected override void OnVoiceTextReady(string text)
    {
    }

    public override Task<bool> FeedVerifyResultAsync(string text, byte[] image) => Task.FromResult(false);
}
