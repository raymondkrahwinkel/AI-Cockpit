using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Claude;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// TTY read-aloud (#35b): unlike the SDK session (<see cref="ReadAloudTests"/>), the TTY panel has no
/// parsed event stream to read prose from — its <see cref="ClaudeTtyViewModel.ReadResponsesAloud"/>
/// toggle instead starts/stops <see cref="ISessionTranscriptReader.ReadAssistantTextAsync"/> against the
/// session's own live JSONL transcript. These tests cover the toggle gate (off → the reader is never
/// even started) and that a tailed line ends up enqueued for TTS the same way a completed SDK turn does.
/// </summary>
public class TtyReadAloudTests
{
    private static readonly ClaudeProfile Work = new("work", "/config/work");

    [Fact]
    public void ReadResponsesAloud_Off_NeverStartsTailingOrEnqueuesAnything()
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<IClaudeTtyLauncher>(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        reader.DidNotReceiveWithAnyArgs().ReadAssistantTextAsync(default!, default, default);
        voicePlaybackQueue.DidNotReceiveWithAnyArgs().Enqueue(default!, default!);
    }

    [Fact]
    public void ReadResponsesAloud_OnWithoutALaunchConfigured_DoesNotStartTailing()
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        var vm = new ClaudeTtyViewModel(Substitute.For<IClaudeTtyLauncher>(), transcriptReader: reader);

        vm.ReadResponsesAloud = true;

        reader.DidNotReceiveWithAnyArgs().ReadAssistantTextAsync(default!, default, default);
    }

    [Fact]
    public async Task ReadResponsesAloud_OnAfterLaunchConfigured_TailsTheConfiguredSession_AndEnqueuesAssistantText()
    {
        Guid? launchedSessionId = null;
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _YieldThenWaitForCancellation("Here is the tty answer.", callInfo.ArgAt<CancellationToken>(2)));
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<IClaudeTtyLauncher>(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);
        vm.LaunchRequested += (_, _, sessionId, _, _, _) => launchedSessionId = sessionId;

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        reader.Received(1).ReadAssistantTextAsync("/config/work", launchedSessionId!.Value, Arg.Any<CancellationToken>());
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Here is the tty answer." })),
            vm.TtsVoiceId);
    }

    [Fact]
    public async Task ReadResponsesAloud_ToggledOff_CancelsTheTailer()
    {
        CancellationToken? capturedToken = null;
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedToken = callInfo.ArgAt<CancellationToken>(2);
                return _YieldThenWaitForCancellation("Here is the tty answer.", capturedToken.Value);
            });
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<IClaudeTtyLauncher>(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;
        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        vm.ReadResponsesAloud = false;

        capturedToken.Should().NotBeNull();
        await _WaitUntilAsync(() => capturedToken!.Value.IsCancellationRequested);
    }

    /// <summary>Stands in for the real tailer: yields once immediately, then blocks (like a live poll loop idling on no new lines) until the caller cancels — so tests can assert both the enqueued text and that toggling off actually cancels the in-flight read.</summary>
    private static async IAsyncEnumerable<string> _YieldThenWaitForCancellation(
        string text, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        yield return text;
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private static async Task _WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        condition().Should().BeTrue("the condition should become true within the poll window");
    }
}
