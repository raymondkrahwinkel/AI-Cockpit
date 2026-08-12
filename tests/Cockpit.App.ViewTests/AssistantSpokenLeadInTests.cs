using Avalonia.Threading;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Assistant;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The lead-in the cockpit speaks when the model went straight to a tool (AC-597) — that it reaches the playback
/// queue at all, and that it reaches it from the assistant's session only.
/// </summary>
[Collection("avalonia")]
public class AssistantSpokenLeadInTests
{
    [Fact]
    public void TheAssistantWithNothingSaidYet_SpeaksALeadIn()
    {
        var queue = new RecordingPlaybackQueue();
        var session = _Assistant(queue, readAloud: true);

        Dispatcher.UIThread.Invoke(session._SpeakLeadInIfTheModelGaveNone);

        Assert.NotEmpty(queue.Spoken);
    }

    [Fact]
    public void AnOrdinarySession_SaysNothingUnasked()
    {
        // An ordinary pane reads its replies aloud; it does not chat. The lead-in exists because the assistant is
        // listened to rather than read, which is true of nothing else in the cockpit.
        var queue = new RecordingPlaybackQueue();
        var session = _Session(queue, readAloud: true);

        Dispatcher.UIThread.Invoke(session._SpeakLeadInIfTheModelGaveNone);

        Assert.Empty(queue.Spoken);
    }

    [Fact]
    public void WithReadAloudOff_NothingIsSpoken_HoweverLongTheSilence()
    {
        var queue = new RecordingPlaybackQueue();
        var session = _Assistant(queue, readAloud: false);

        Dispatcher.UIThread.Invoke(session._SpeakLeadInIfTheModelGaveNone);

        Assert.Empty(queue.Spoken);
    }

    [Fact]
    public void OnceSomethingHasBeenSaidThisTurn_NoLeadInIsAddedOnTopOfIt()
    {
        // The failure worth ruling out: a turn that opened with "I'll look at the release desk" followed a beat
        // later by "one moment, let me look" — two lead-ins, which is worse than the silence it replaced.
        var queue = new RecordingPlaybackQueue();
        var session = _Assistant(queue, readAloud: true);

        Dispatcher.UIThread.Invoke(() =>
        {
            session._SpeakLeadInIfTheModelGaveNone();
            session._SpeakLeadInIfTheModelGaveNone();
        });

        Assert.Single(queue.Spoken);
    }

    private static SessionViewModel _Assistant(IVoicePlaybackQueue queue, bool readAloud)
    {
        var session = _Session(queue, readAloud);
        Dispatcher.UIThread.Invoke(() => session.AdoptPaneId(AssistantIdentity.PaneId));
        return session;
    }

    private static SessionViewModel _Session(IVoicePlaybackQueue queue, bool readAloud) =>
        Dispatcher.UIThread.Invoke(() => new SessionViewModel(
            Substitute.For<ISessionManager>(),
            voicePlaybackQueue: queue)
        {
            ReadResponsesAloud = readAloud,
            ReadAloudLanguage = "nl",
        });

    private sealed class RecordingPlaybackQueue : IVoicePlaybackQueue
    {
        public List<string> Spoken { get; } = [];

        public int Generation => 0;

        public VoicePlaybackSource ActiveSource => VoicePlaybackSource.Session;

        public event EventHandler<bool>? PlaybackActiveChanged;

        public event EventHandler? SpeakingStarted;

        public void Enqueue(IReadOnlyList<string> sentences, int speakerId, string language, VoicePlaybackSource source = VoicePlaybackSource.Session) =>
            Spoken.AddRange(sentences);

        public void Enqueue(IReadOnlyList<SpeechSegment> segments, int speakerId, VoicePlaybackSource source = VoicePlaybackSource.Session) =>
            Spoken.AddRange(segments.SelectMany(segment => segment.Sentences));

        public void NotifyPreparing(VoicePlaybackSource source = VoicePlaybackSource.Session)
        {
            PlaybackActiveChanged?.Invoke(this, true);
            SpeakingStarted?.Invoke(this, EventArgs.Empty);
        }

        public void StopAll() => PlaybackActiveChanged?.Invoke(this, false);
    }
}
