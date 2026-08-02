using Cockpit.App.ViewModels;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The SDK half of the spawn brief. A chat session that has not started yet loses an
/// <see cref="SessionPanelViewModel.InjectAndSubmit"/> differently from a TTY one — nothing vanishes, but the text
/// stays sitting in the composer and the transcript gains "The session has not started yet — nothing was sent.",
/// which is a session the operator was told had started and which is not working. So it is not a delivery either,
/// and <see cref="SessionPanelViewModel.SubmitPromptWhenReady"/> has to cover this kind too.
/// </summary>
[Collection("avalonia")]
public class SpawnedSessionBriefDeliveryTests
{
    [Fact]
    public void InjectAndSubmit_IntoASessionThatHasNotStarted_StrandsTheTextAndSaysSoInTheTranscript() => HeadlessAvalonia.Run(() =>
    {
        // The control: this is what the spawn path used to do, and what it does to a pane whose runtime is not up.
        var session = new SessionViewModel();

        session.InjectAndSubmit("start on the migration");

        Assert.Equal("start on the migration", session.InputText);
        Assert.Contains(
            session.Transcript,
            entry => entry.Kind == TranscriptEntryKind.Error && entry.Text.Contains("has not started yet"));
    });

    [Fact]
    public void SubmitPromptWhenReady_IntoASessionThatHasNotStarted_HoldsTheBriefInsteadOfFailingIntoTheTranscript() => HeadlessAvalonia.Run(() =>
    {
        var session = new SessionViewModel();

        var wentOutNow = session.SubmitPromptWhenReady("start on the migration");

        Assert.False(wentOutNow);
        Assert.True(session.HasPromptWaitingToBeDelivered);
        Assert.Equal(string.Empty, session.InputText);
        Assert.DoesNotContain(session.Transcript, entry => entry.Kind == TranscriptEntryKind.Error);
    });
}
