using Avalonia.Threading;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-294: <c>watch_session</c>'s <c>stuck</c> and <c>pattern</c> over a TTY session. The probe answered for
/// <see cref="SessionViewModel"/> alone and handed every other pane <c>HasTranscript: false</c>, so both were
/// refused at arming for "keeps no transcript in the cockpit" — a reason AC-609 ended. Until now the operator
/// could not see that a terminal session had stopped writing: the pane that looks finished while it is wedged.
/// </summary>
[Collection("avalonia")]
public class SessionWatcherTtyProbeTests
{
    private static TtyViewModel _Tty(ISessionTranscriptReader reader) =>
        new(Substitute.For<ITtyLauncher>(), Substitute.For<ITtySessionProviderResolver>(), transcriptReader: reader);

    private static ISessionTranscriptReader _Reader(SessionTranscriptSlice slice)
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.ReadEntries(Arg.Any<SessionProfile?>(), Arg.Any<string?>(), Arg.Any<int>()).Returns(slice);
        return reader;
    }

    [Fact]
    public async Task TheProbe_ReadsATtySessionsRowsBackOffTheFileItsCliWrote()
    {
        var slice = new SessionTranscriptSlice(
            [
                new SessionTranscriptEntry("AssistantText", "cutting the branch", null),
                new SessionTranscriptEntry("ToolUse", "error: the build fell over", null),
            ],
            TotalEntries: 7);

        var (probe, paneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var tty = _Tty(_Reader(slice));
            tty.TrackLimits("session-7.json", [], null);
            cockpit.Sessions.Add(tty);
            return (SessionWatcher.ProbeOf(cockpit), tty.PaneId);
        });

        // Asked at row 6 of 7: one row is new, and it is the last one read back — the same "everything after what
        // you have already seen" the SDK arm slices out of its in-memory transcript.
        var pane = await probe(paneId, 6);

        Assert.NotNull(pane);
        Assert.True(pane!.HasTranscript);
        Assert.Equal(7, pane.TranscriptRows);
        Assert.Equal(["error: the build fell over"], pane.NewRows);
        Assert.Equal(["cutting the branch", "error: the build fell over"], pane.LastRows);
    }

    // AC-294: `HasTranscript` asks the route, never the content. A watch is in practice armed straight after
    // start_agent, when the CLI has not written a word yet, and gating on content would refuse it exactly there.
    // `StatusFile` is set the moment the pty is up, which is the line between empty and unreadable.
    [Fact]
    public async Task TheProbe_CallsAnEmptyButReadableTtySessionWatchable()
    {
        var (probe, paneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var tty = _Tty(_Reader(SessionTranscriptSlice.Empty));
            tty.TrackLimits("session-42.json", [], null);
            cockpit.Sessions.Add(tty);
            return (SessionWatcher.ProbeOf(cockpit), tty.PaneId);
        });

        var pane = await probe(paneId, 0);

        Assert.NotNull(pane);
        Assert.True(pane!.HasTranscript);
        Assert.Equal(0, pane.TranscriptRows);
    }

    [Fact]
    public async Task TheProbe_ReportsNoTranscriptForAProviderThatRecordsNothingReadable()
    {
        // Codex is the live example: it has an IPluginTranscriptReader that tails activity but never implements
        // ReadEntries, and its TUI installs no statusline relay, so nothing names a record for it. Refused at
        // arming rather than armed and forever silent — a watch that reads as coverage is worse than no watch.
        var (probe, paneId) = Dispatcher.UIThread.Invoke(() =>
        {
            var cockpit = new CockpitViewModel();
            var tty = _Tty(_Reader(SessionTranscriptSlice.Empty));
            cockpit.Sessions.Add(tty);
            return (SessionWatcher.ProbeOf(cockpit), tty.PaneId);
        });

        var pane = await probe(paneId, 0);

        Assert.NotNull(pane);
        Assert.False(pane!.HasTranscript);
    }
}
