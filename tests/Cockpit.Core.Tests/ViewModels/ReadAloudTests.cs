using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Read-aloud (#35): the per-session <see cref="SessionViewModel.ReadResponsesAloud"/> toggle
/// gates the turn-completion trigger, and a push-to-talk hold interrupts whatever is queued/playing.
/// </summary>
public class ReadAloudTests
{
    [Fact]
    public void TurnCompleted_ReadAloudOff_NeverEnqueuesAnything()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = false,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Empty(voicePlaybackQueue.ReceivedCalls());
    }

    [Fact]
    public void TurnCompleted_ReadAloudOn_EnqueuesTheTurnsProse()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { TtsVoiceSid = 3 });
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voiceSettingsStore: voiceSettingsStore, voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Here is the answer." })),
            3,
            "en");
    }

    [Fact]
    public void TurnCompleted_NoAssistantTextThisTurn_EnqueuesNothing_EvenWithReadAloudOn()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        Assert.Empty(voicePlaybackQueue.ReceivedCalls());
    }

    [Fact]
    public void TurnCompleted_AsOneUtterance_JoinsTheSentencesIntoOneEnqueue()
    {
        // The gaps the operator hears are the boundaries between clips: the queue synthesises one sentence ahead
        // while the previous plays, and on this machine synthesis is slower than playback, so every full stop
        // opens a hole that look-ahead cannot close. One sentence in means one synthesis and one continuous clip.
        // This is the assistant's setting (AssistantSessionHost sets it) and the reason it sounds like speech
        // rather than a list, so it is pinned here on the turn path — the only route left after AC-546.
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
            ReadAloudAsOneUtterance = true,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "First sentence. Second sentence. Third sentence." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.Count == 1),
            vm.TtsVoiceSid,
            "en");
    }

    [Fact]
    public void TurnCompleted_NotAsOneUtterance_KeepsTheSentencesSeparate()
    {
        // The other side of the flag: a reply can run for paragraphs, and one synthesis there would be a long
        // silence before the first word. Joining must stay something the caller asks for, never the default.
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "First sentence. Second sentence. Third sentence." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.Count == 3),
            vm.TtsVoiceSid,
            "en");
    }

    [Fact]
    public async Task BeginVoiceHold_InterruptsWhateverIsQueuedOrPlaying()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePushToTalk, voiceSettingsStore, voicePlaybackQueue);
        await _WaitUntilAsync(() => vm.VoiceEnabled);

        Assert.True(vm.BeginVoiceHold());

        voicePlaybackQueue.Received(1).StopAll();
    }

    [Fact]
    public void PermissionRequested_MidTurn_ReadAloudOn_FlushesTheLeadIn_ThenTurnCompletedDoesNotRepeatIt()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { TtsVoiceSid = 3 });
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voiceSettingsStore: voiceSettingsStore, voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Let me check that for you." });
        vm.Apply(new PermissionRequested { SessionId = "S1", ToolUseId = "t1", ToolName = "Bash", InputJson = "{}" });

        // AC-97: the lead-in is spoken the moment the tool needs approval, not held back until the operator answers.
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Let me check that for you." })), 3, "en");

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        // TurnCompleted must not speak the already-flushed lead-in a second time.
        voicePlaybackQueue.Received(1).Enqueue(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<string>());
    }

    [Fact]
    public void Question_MidTurn_ReadAloudOff_EnqueuesNothing()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = false,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is a thought." });
        vm.Apply(new Question { SessionId = "S1", Text = "Which option do you prefer?" });

        Assert.Empty(voicePlaybackQueue.ReceivedCalls());
    }

    [Fact]
    public void AfterAMidTurnQuestion_TurnCompletedSpeaksTheRestThatStreamedInAfterIt()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { TtsVoiceSid = 3 });
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()), voiceSettingsStore: voiceSettingsStore, voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        // The real Claude driver never emits AssistantTextCompleted: a turn's deltas keep appending to one growing
        // entry, including the text that streams in after a mid-turn question. This drives that exact shape — a
        // flush that counted entries instead of text offset would mark the whole entry spoken at the question and
        // silently drop "Now, the result.".
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "First, the setup. " });
        vm.Apply(new Question { SessionId = "S1", Text = "Ready?" });
        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Now, the result." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "First, the setup." })), 3, "en");
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Now, the result." })), 3, "en");
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
