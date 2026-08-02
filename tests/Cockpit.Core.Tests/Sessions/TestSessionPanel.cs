using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.Sessions;

/// <summary>
/// A session panel that records what was sent into it, so a scheduled resume can be tested without a runtime or
/// a pty behind it. The rest of the panel contract is inert here — this stands in for a session, it is not one.
/// </summary>
internal sealed class TestSessionPanel : SessionPanelViewModel
{
    public List<string> Sent { get; } = [];

    // Defaults to a live, ready session — every existing scheduled-resume test assumes delivery works and gates
    // on IsDue/HasLapsed instead. Set false to stand in for a restored pane that was never started (AC-410),
    // where CanTakeAPrompt is what ScheduledResumeCoordinator.RunDueAsync must refuse to send into.
    public bool CanTakeAPromptOverride { get; set; } = true;

    public override bool CanTakeAPrompt => CanTakeAPromptOverride;

    public override Task<bool> SendPromptAsync(string prompt)
    {
        Sent.Add(prompt);
        return Task.FromResult(true);
    }

    /// <summary>Everything this panel's own input surface was handed, in order — "text" for an injection, "submit" for the send gesture that follows it. What <c>InjectAndSubmit</c> does, seen from the inside.</summary>
    public List<string> Injected { get; } = [];

    /// <summary>Lets a test drive the flush from outside, standing in for the moment a real session kind's readiness changes (a TTY's pty sink arriving, an SDK runtime coming up).</summary>
    public void BecomeReady()
    {
        CanTakeAPromptOverride = true;
        DeliverHeldPrompt();
    }

    /// <summary>The same announcement without becoming ready — a launch that settled as a failure.</summary>
    public void DeliverHeldPromptForTest() => DeliverHeldPrompt();

    protected override ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    protected override void OnVoiceTextReady(string text) => Injected.Add(text);

    protected override void OnVoiceSubmitRequested() => Injected.Add("submit");

    public override Task<bool> FeedVerifyResultAsync(string text, byte[] image) => Task.FromResult(false);

    protected override Task<string?> OnScreenshotCapturedAsync(byte[] screenshotPng) => Task.FromResult<string?>(null);
}
