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

    [Fact]
    public async Task TheProbe_ReportsNoTranscriptForATtySessionNothingCanBeReadBackFrom()
    {
        // A record that cannot be named, cannot be opened, or holds nothing yet. Reported as no transcript rather
        // than as an empty one, so arming `stuck` on it is refused now instead of reporting a stall from the first
        // tick on — a watch armed on nothing is worse than no watch, because it reads as coverage.
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
        Assert.Equal(0, pane.TranscriptRows);
    }
}
