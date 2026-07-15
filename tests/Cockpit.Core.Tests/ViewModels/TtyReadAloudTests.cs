using System.Runtime.CompilerServices;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// TTY read-aloud (#35b): unlike the SDK session (<see cref="ReadAloudTests"/>), the TTY panel has no
/// parsed event stream to read prose from — its <see cref="ClaudeTtyViewModel.ReadResponsesAloud"/>
/// toggle instead starts/stops <see cref="ISessionTranscriptReader.ReadAssistantTextAsync"/> against the
/// session's own live JSONL transcript, located as the new file that appears after launch (the id is not
/// forced). These tests cover the toggle gate (off → the reader is never even started) and that a tailed
/// line ends up enqueued for TTS the same way a completed SDK turn does.
/// </summary>
public class TtyReadAloudTests
{
    // A migrated Claude profile is a plugin profile now; its config dir is reconstructed via SessionProfile.Claude, so
    // this exercises the real post-Fase-4 shape rather than the legacy in-tree ClaudeConfig that no longer occurs.
    private static readonly SessionProfile Work = new("work", ClaudePluginProfile.Create("/config/work", null));

    [Fact]
    public void ReadResponsesAloud_Off_NeverStartsTailingOrEnqueuesAnything()
    {
        var reader = _Reader();
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        reader.DidNotReceiveWithAnyArgs().ReadAssistantTextAsync(default!, default!, default);
        voicePlaybackQueue.DidNotReceiveWithAnyArgs().Enqueue(default!, default!);
    }

    [Fact]
    public void ReadResponsesAloud_OnWithoutALaunchConfigured_DoesNotStartTailing()
    {
        var reader = _Reader();
        var vm = new ClaudeTtyViewModel(Substitute.For<ITtyLauncher>(), _Resolver(), transcriptReader: reader);

        vm.ReadResponsesAloud = true;

        reader.DidNotReceiveWithAnyArgs().ReadAssistantTextAsync(default!, default!, default);
    }

    [Fact]
    public async Task ReadResponsesAloud_OnAfterLaunchConfigured_TailsTheConfiguredSession_AndEnqueuesAssistantText()
    {
        var reader = _Reader();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _YieldThenWaitForCancellation("Here is the tty answer.", callInfo.ArgAt<CancellationToken>(2)));
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);

        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        reader.Received(1).ReadAssistantTextAsync("/config/work", Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Here is the tty answer." })),
            vm.TtsVoiceId);
    }

    [Fact]
    public async Task ReadResponsesAloud_OnWithoutAProfile_StillTailsTheDefaultConfigDirSession_AndEnqueues()
    {
        var reader = _Reader();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => _YieldThenWaitForCancellation("Profile-less answer.", callInfo.ArgAt<CancellationToken>(2)));
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);

        vm.LaunchConfigured(profile: null, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;

        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        reader.Received(1).ReadAssistantTextAsync(
            Arg.Is<string>(dir => !string.IsNullOrWhiteSpace(dir)), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>());
        voicePlaybackQueue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Profile-less answer." })),
            vm.TtsVoiceId);
    }

    [Fact]
    public async Task DisposeAsync_WhileReadingAloud_StopsPlaybackSoAClosedSessionGoesSilent()
    {
        var reader = _Reader();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _EmptyTranscript());
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;

        await vm.DisposeAsync();

        voicePlaybackQueue.Received(1).StopAll();
    }

    [Fact]
    public async Task DisposeAsync_WhenNotReadingAloud_LeavesOtherSessionsPlaybackUntouched()
    {
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: _Reader());
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");

        await vm.DisposeAsync();

        voicePlaybackQueue.DidNotReceive().StopAll();
    }

    [Fact]
    public async Task ReadResponsesAloud_ToggledOff_CancelsTheTailer()
    {
        CancellationToken? capturedToken = null;
        var reader = _Reader();
        reader.ReadAssistantTextAsync(Arg.Any<string>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedToken = callInfo.ArgAt<CancellationToken>(2);
                return _YieldThenWaitForCancellation("Here is the tty answer.", capturedToken.Value);
            });
        var voicePlaybackQueue = Substitute.For<IVoicePlaybackQueue>();
        var vm = new ClaudeTtyViewModel(
            Substitute.For<ITtyLauncher>(), _Resolver(), voicePlaybackQueue: voicePlaybackQueue, transcriptReader: reader);
        vm.LaunchConfigured(Work, "default", "sonnet", "medium");
        vm.ReadResponsesAloud = true;
        await _WaitUntilAsync(() => voicePlaybackQueue.ReceivedCalls().Any());

        vm.ReadResponsesAloud = false;

        capturedToken.Should().NotBeNull();
        await _WaitUntilAsync(() => capturedToken!.Value.IsCancellationRequested);
    }

    /// <summary>Resolves any profile (including none) to a fresh provider substitute — same as the real resolver does for a Claude profile or a profile-less session.</summary>
    private static ITtySessionProviderResolver _Resolver()
    {
        var resolver = Substitute.For<ITtySessionProviderResolver>();
        resolver.Resolve(Arg.Any<SessionProfile?>()).Returns(Substitute.For<ITtySessionProvider>());
        return resolver;
    }

    /// <summary>A transcript reader whose launch snapshot is empty, so the VM's baseline is non-null and the tailer actually starts.</summary>
    private static ISessionTranscriptReader _Reader()
    {
        var reader = Substitute.For<ISessionTranscriptReader>();
        reader.SnapshotTranscripts(Arg.Any<string>()).Returns(new HashSet<string>());
        return reader;
    }

#pragma warning disable CS1998 // async iterator with no awaits — an immediately-completing empty stream
    private static async IAsyncEnumerable<string> _EmptyTranscript()
    {
        yield break;
    }
#pragma warning restore CS1998

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
