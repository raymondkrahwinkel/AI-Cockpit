using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-1373: the session registry follows the cockpit's panes, SDK and TTY alike, and a handle taken from it
/// reaches its pane from a request thread. Every read here happens off the UI thread, the way the backend will.
/// </summary>
[Collection("avalonia")]
public class SessionRegistryFeedTests
{
    [Fact]
    public async Task ClosingAPane_TakesItOutOfTheRegistry_AndLeavesTheOthers()
    {
        var sessions = new SessionRegistry();
        var (cockpit, closing, staying) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel(sessionRegistry: sessions);
            return (cockpit, cockpit.Sessions[0], cockpit.Sessions[1]);
        });
        Assert.NotNull(sessions.Find(closing.PaneId));

        await Dispatcher.UIThread.InvokeAsync(() => cockpit.CloseSessionCommand.ExecuteAsync(closing));

        Assert.Null(sessions.Find(closing.PaneId));
        Assert.NotNull(sessions.Find(staying.PaneId));
    }

    // CLAUDE.md, SDK↔TTY: the statusline, reading a transcript back and pushing text plus Enter belong on both routes.
    [Fact]
    public async Task ATtyPane_IsReachableThroughItsHandle()
    {
        var sessions = new SessionRegistry();
        var written = new List<string>();
        var slice = new SessionTranscriptSlice([new SessionTranscriptEntry("AssistantText", "cutting the branch", null)], TotalEntries: 7);
        var paneId = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel(sessionRegistry: sessions);
            var reader = Substitute.For<ISessionTranscriptReader>();
            reader.ReadEntries(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), 1).Returns(slice);
            var tty = new TtyViewModel(Substitute.For<ITtyLauncher>(), Substitute.For<ITtySessionProviderResolver>(), transcriptReader: reader)
            {
                Statusline = "AC-1373",
                PromptSink = written.Add,
            };
            tty.TrackLimits("session-7.json", [], null);
            tty.MarkHostedTuiReady();
            cockpit.Sessions.Add(tty);
            return tty.PaneId;
        });

        var handle = sessions.Find(paneId);

        Assert.NotNull(handle);
        Assert.Equal("AC-1373", handle.Statusline);
        Assert.Same(slice, await handle.ReadTranscriptAsync(1));
        Assert.True(await handle.SendPromptAsync("carry on"));
        Assert.Equal(["carry on\r"], Dispatcher.UIThread.Invoke(() => written.ToList()));
    }

    [Fact]
    public async Task AnSdkPanesTranscript_IsReadAsItsLastRowsAndTheTotal()
    {
        var sessions = new SessionRegistry();
        var paneId = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel(sessionRegistry: sessions);
            var sdk = new SessionViewModel();
            // The parameterless constructor seeds design-time rows; this test counts its own.
            sdk.Transcript.Clear();
            sdk.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "first"));
            sdk.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "second"));
            sdk.Transcript.Add(new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "third"));
            cockpit.Sessions.Add(sdk);
            return sdk.PaneId;
        });

        var handle = sessions.Find(paneId);
        Assert.NotNull(handle);
        var slice = await handle.ReadTranscriptAsync(2);

        Assert.Equal(3, slice.TotalEntries);
        Assert.Equal(
            [new SessionTranscriptEntry("UserText", "second", null), new SessionTranscriptEntry("AssistantText", "third", null)],
            slice.Entries);
    }
}
