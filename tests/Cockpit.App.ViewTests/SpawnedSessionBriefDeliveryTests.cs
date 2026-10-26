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
    public void DeliverySeams_DistinguishComposerPlacementFromAHeldFirstTurn() => HeadlessAvalonia.Run(() =>
    {
        var direct = new SessionViewModel();

        direct.InjectAndSubmit("start on the migration");

        Assert.Equal("start on the migration", direct.InputText);
        Assert.Contains(
            direct.Transcript,
            entry => entry.Kind == TranscriptEntryKind.Error && entry.Text.Contains("has not started yet"));
        var held = new SessionViewModel();

        var wentOutNow = held.SubmitPromptWhenReady("start on the migration");

        Assert.False(wentOutNow);
        Assert.True(held.HasPromptWaitingToBeDelivered);
        Assert.Equal(string.Empty, held.InputText);
        Assert.DoesNotContain(held.Transcript, entry => entry.Kind == TranscriptEntryKind.Error);
    });
}
