using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Sessions;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Read-aloud (#35): the per-session <see cref="SessionViewModel.ReadResponsesAloud"/> toggle
/// gates the turn-completion trigger, the per-row <see cref="SessionViewModel.ReadAloudCommand"/>
/// works regardless of that toggle, and a push-to-talk hold interrupts whatever is queued/playing.
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

        voicePlaybackQueue.ReceivedCalls().Should().BeEmpty();
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

        voicePlaybackQueue.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void ReadAloudCommand_OnAssistantRow_Enqueues_EvenWhenTheSessionToggleIsOff()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = false,
        };
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Read this one.");

        vm.ReadAloudCommand.Execute(entry);

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Read this one." })),
            vm.TtsVoiceSid,
            "en");
    }

    [Fact]
    public void ReadAloudCommand_OnANonAssistantRow_DoesNothing()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new SessionViewModel(new SessionManager(Substitute.For<ISessionDriverFactory>()), voicePlaybackQueue: voicePlaybackQueue);
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "not an assistant reply");

        vm.ReadAloudCommand.Execute(entry);

        voicePlaybackQueue.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TurnCompleted_NaturalizeMode_SplitsMarkedLanguagesIntoSegments()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings
        {
            ReadAloudMode = ReadAloudMode.Naturalized,
            TtsVoiceSid = 3,
        });
        var cleanupService = Substitute.For<ITranscriptCleanupService>();
        cleanupService.NaturalizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("[[en]]Here is the answer. [[nl]]Dit is het antwoord.");
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()),
            voiceSettingsStore: voiceSettingsStore,
            voicePlaybackQueue: voicePlaybackQueue,
            cleanupService: cleanupService)
        {
            ReadResponsesAloud = true,
        };
        await _WaitUntilAsync(() => vm.ReadAloudMode == ReadAloudMode.Naturalized);

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<SpeechSegment>>(segments =>
                segments.Count == 2 &&
                segments[0].Language == "en" &&
                segments[1].Language == "nl"),
            3);
    }

    [Fact]
    public async Task TurnCompleted_SummarizeMode_SummarizesTheReplyBeforeSpeaking()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings
        {
            ReadAloudMode = ReadAloudMode.Summarized,
            TtsVoiceSid = 1,
        });
        var cleanupService = Substitute.For<ITranscriptCleanupService>();
        cleanupService.SummarizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("[[en]]Short summary.");
        var vm = new SessionViewModel(
            new SessionManager(Substitute.For<ISessionDriverFactory>()),
            voiceSettingsStore: voiceSettingsStore,
            voicePlaybackQueue: voicePlaybackQueue,
            cleanupService: cleanupService)
        {
            ReadResponsesAloud = true,
        };
        await _WaitUntilAsync(() => vm.ReadAloudMode == ReadAloudMode.Summarized);

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "A long reply with lots of detail." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        await cleanupService.Received(1).SummarizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cleanupService.DidNotReceiveWithAnyArgs().NaturalizeForSpeechAsync(default!, default);
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<SpeechSegment>>(segments => segments.Count == 1 && segments[0].Language == "en"),
            1);
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

        vm.BeginVoiceHold().Should().BeTrue();

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

        voicePlaybackQueue.ReceivedCalls().Should().BeEmpty();
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
