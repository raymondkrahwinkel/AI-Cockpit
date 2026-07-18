using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="ReadAloudPipeline"/>: the one path assistant text takes to become queued speech. The behaviour that
/// matters here is the barge-in guard — if the operator interrupts while the local LLM is still rewriting the reply,
/// the now-stale batch must be dropped rather than spoken over the interrupt they just made.
/// </summary>
public class ReadAloudPipelineTests
{
    [Fact]
    public async Task Verbatim_EnqueuesTheExtractedProse_WithSpeakerAndLanguage()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();

        await ReadAloudPipeline.SpeakAsync(queue, cleanupService: null, "Open the settings.", ReadAloudMode.Verbatim, speakerId: 3, language: "en");

        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<string>>(sentences => sentences.SequenceEqual(new[] { "Open the settings." })),
            3,
            "en");
    }

    [Fact]
    public async Task NothingToSay_NeverTouchesTheQueue()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();

        await ReadAloudPipeline.SpeakAsync(queue, cleanupService: null, "   ", ReadAloudMode.Verbatim, speakerId: 1, language: "en");

        queue.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Naturalized_NoBargeIn_EnqueuesTheRoutedSegments()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        queue.Generation.Returns(0);
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.NaturalizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("[[en]]Here it is. [[nl]]Hier is het.");

        await ReadAloudPipeline.SpeakAsync(queue, cleanup, "Here it is.", ReadAloudMode.Naturalized, speakerId: 3, language: "en");

        queue.Received(1).Enqueue(
            Arg.Is<IReadOnlyList<SpeechSegment>>(segments => segments.Count == 2 && segments[0].Language == "en" && segments[1].Language == "nl"),
            3);
    }

    [Fact]
    public async Task Naturalized_BargeInDuringRewrite_DropsTheStaleBatch()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        // Generation is read once before the rewrite (0) and once after (1) — a StopAll bumped it mid-rewrite.
        queue.Generation.Returns(0, 1);
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.NaturalizeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("[[en]]Here it is.");

        await ReadAloudPipeline.SpeakAsync(queue, cleanup, "Here it is.", ReadAloudMode.Naturalized, speakerId: 3, language: "en");

        queue.DidNotReceive().Enqueue(Arg.Any<IReadOnlyList<SpeechSegment>>(), Arg.Any<int>());
        queue.DidNotReceive().Enqueue(Arg.Any<IReadOnlyList<string>>(), Arg.Any<int>(), Arg.Any<string>());
    }
}
