using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using FluentAssertions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// <see cref="TurnAcknowledgmentPipeline"/> (AC-99): the turn-start "let me take a look". Off says nothing, the
/// preset mode voices a rotating phrase with no model call, and the local-LLM mode speaks a generated line but
/// falls back to a preset when the model returns nothing.
/// </summary>
public class TurnAcknowledgmentPipelineTests
{
    [Fact]
    public async Task Off_SaysNothing()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();

        var next = await TurnAcknowledgmentPipeline.SpeakAsync(queue, cleanupService: null, TurnAckMode.Off, phraseIndex: 0, "do the thing", speakerId: 1, language: "en");

        queue.ReceivedCalls().Should().BeEmpty();
        next.Should().Be(0);
    }

    [Fact]
    public async Task InstantPhrases_SpeaksAPreset_AndAdvancesTheRotation()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var presets = TurnAcknowledgmentPhrases.For("en");

        var next = await TurnAcknowledgmentPipeline.SpeakAsync(queue, cleanupService: null, TurnAckMode.InstantPhrases, phraseIndex: 0, "do the thing", speakerId: 2, language: "en");

        queue.Received(1).Enqueue(Arg.Is<IReadOnlyList<string>>(s => s.Count == 1 && s[0] == presets[0]), 2, "en");
        next.Should().Be(1);
    }

    [Fact]
    public async Task InstantPhrases_BackToBackTurns_DoNotRepeatTheSamePhrase()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var presets = TurnAcknowledgmentPhrases.For("nl");

        var afterFirst = await TurnAcknowledgmentPipeline.SpeakAsync(queue, null, TurnAckMode.InstantPhrases, 0, "x", 1, "nl");
        await TurnAcknowledgmentPipeline.SpeakAsync(queue, null, TurnAckMode.InstantPhrases, afterFirst, "y", 1, "nl");

        queue.Received(1).Enqueue(Arg.Is<IReadOnlyList<string>>(s => s[0] == presets[0]), 1, "nl");
        queue.Received(1).Enqueue(Arg.Is<IReadOnlyList<string>>(s => s[0] == presets[1]), 1, "nl");
    }

    [Fact]
    public async Task LocalLlm_SpeaksTheGeneratedLine_WithoutConsumingAPreset()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.AcknowledgeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("On it, checking now.");

        var next = await TurnAcknowledgmentPipeline.SpeakAsync(queue, cleanup, TurnAckMode.LocalLlm, phraseIndex: 0, "do the thing", speakerId: 3, language: "en");

        queue.Received(1).Enqueue(Arg.Is<IReadOnlyList<string>>(s => s.Count == 1 && s[0] == "On it, checking now."), 3, "en");
        next.Should().Be(0);
    }

    [Fact]
    public async Task LocalLlm_ModelReturnsNothing_FallsBackToAPreset()
    {
        var queue = Substitute.For<IVoicePlaybackQueue>();
        var presets = TurnAcknowledgmentPhrases.For("en");
        var cleanup = Substitute.For<ITranscriptCleanupService>();
        cleanup.AcknowledgeForSpeechAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("   ");

        var next = await TurnAcknowledgmentPipeline.SpeakAsync(queue, cleanup, TurnAckMode.LocalLlm, phraseIndex: 0, "do the thing", speakerId: 3, language: "en");

        queue.Received(1).Enqueue(Arg.Is<IReadOnlyList<string>>(s => s[0] == presets[0]), 3, "en");
        next.Should().Be(1);
    }
}
