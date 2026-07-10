using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Claude;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// Read-aloud (#35): the per-session <see cref="ClaudeSessionViewModel.ReadResponsesAloud"/> toggle
/// gates the turn-completion trigger, the per-row <see cref="ClaudeSessionViewModel.ReadAloudCommand"/>
/// works regardless of that toggle, and a push-to-talk hold interrupts whatever is queued/playing.
/// </summary>
public class ReadAloudTests
{
    [Fact]
    public void TurnCompleted_ReadAloudOff_NeverEnqueuesAnything()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeSessionViewModel(Substitute.For<ISessionDriverFactory>(), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = false,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.DidNotReceiveWithAnyArgs().Enqueue(default!, default!);
    }

    [Fact]
    public void TurnCompleted_ReadAloudOn_EnqueuesTheTurnsProse()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { TtsVoiceId = "nl_NL-ronnie-medium" });
        var vm = new ClaudeSessionViewModel(
            Substitute.For<ISessionDriverFactory>(), voiceSettingsStore: voiceSettingsStore, voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Here is the answer." })),
            "nl_NL-ronnie-medium");
    }

    [Fact]
    public void TurnCompleted_NoAssistantTextThisTurn_EnqueuesNothing_EvenWithReadAloudOn()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeSessionViewModel(Substitute.For<ISessionDriverFactory>(), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = true,
        };

        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        voicePlaybackQueue.DidNotReceiveWithAnyArgs().Enqueue(default!, default!);
    }

    [Fact]
    public void ReadAloudCommand_OnAssistantRow_Enqueues_EvenWhenTheSessionToggleIsOff()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeSessionViewModel(Substitute.For<ISessionDriverFactory>(), voicePlaybackQueue: voicePlaybackQueue)
        {
            ReadResponsesAloud = false,
        };
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "Read this one.");

        vm.ReadAloudCommand.Execute(entry);

        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Read this one." })),
            vm.TtsVoiceId);
    }

    [Fact]
    public void ReadAloudCommand_OnANonAssistantRow_DoesNothing()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeSessionViewModel(Substitute.For<ISessionDriverFactory>(), voicePlaybackQueue: voicePlaybackQueue);
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "not an assistant reply");

        vm.ReadAloudCommand.Execute(entry);

        voicePlaybackQueue.DidNotReceiveWithAnyArgs().Enqueue(default!, default!);
    }

    [Fact]
    public async Task TurnCompleted_NaturalizeOn_RoutesMarkedLanguagesToTheirVoices()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings
        {
            NaturalizeReadAloud = true,
            TtsVoiceId = "en_US-lessac-medium",
            TtsVoiceIdDutch = "nl_NL-ronnie-medium",
        });
        var cleanupService = Substitute.For<ITranscriptCleanupService>();
        cleanupService.NaturalizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("[[en]]Here is the answer. [[nl]]Dit is het antwoord.");
        var vm = new ClaudeSessionViewModel(
            Substitute.For<ISessionDriverFactory>(),
            voiceSettingsStore: voiceSettingsStore,
            voicePlaybackQueue: voicePlaybackQueue,
            cleanupService: cleanupService)
        {
            ReadResponsesAloud = true,
        };
        await _WaitUntilAsync(() => vm.NaturalizeReadAloud);

        vm.Apply(new AssistantTextDelta { SessionId = "S1", BlockIndex = 0, Text = "Here is the answer." });
        vm.Apply(new TurnCompleted { SessionId = "S1", Subtype = "success", Result = "done", IsError = false });

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        voicePlaybackQueue.Received(1).Enqueue(Arg.Is<IReadOnlyList<SpeechSegment>>(segments =>
            segments.Count == 2 &&
            segments[0].VoiceId == "en_US-lessac-medium" &&
            segments[1].VoiceId == "nl_NL-ronnie-medium"));
    }

    [Fact]
    public async Task BeginVoiceHold_InterruptsWhateverIsQueuedOrPlaying()
    {
        var voicePushToTalk = Substitute.For<IVoicePushToTalkService>();
        voicePushToTalk.BeginHold().Returns(true);
        var voiceSettingsStore = Substitute.For<IVoiceSettingsStore>();
        voiceSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(new VoiceSettings { IsEnabled = true });
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeSessionViewModel(
            Substitute.For<ISessionDriverFactory>(), voicePushToTalk, voiceSettingsStore, voicePlaybackQueue);
        await _WaitUntilAsync(() => vm.VoiceEnabled);

        vm.BeginVoiceHold().Should().BeTrue();

        voicePlaybackQueue.Received(1).StopAll();
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 50 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
