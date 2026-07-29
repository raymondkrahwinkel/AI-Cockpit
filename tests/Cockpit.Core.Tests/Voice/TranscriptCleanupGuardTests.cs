using Cockpit.Core.Voice;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// Pure safety-net decisions ported from WisperFlow's <c>cleanup.py</c>: skip cleanup for a too-short
/// utterance, and reject cleaned output that looks like a hallucination (empty, or grown far past the
/// raw transcript's length) — the "bij twijfel → rauwe transcript" behaviour.
/// </summary>
public class TranscriptCleanupGuardTests
{
    private static readonly TranscriptCleanupOptions Options = new();

    [Theory]
    [InlineData("yes")]
    [InlineData("no thanks")]
    public void ShouldSkipCleanup_BelowMinWordCount_ReturnsTrue(string rawText)
    {
        Assert.True(TranscriptCleanupGuard.ShouldSkipCleanup(rawText, Options));
    }

    [Fact]
    public void ShouldSkipCleanup_AtOrAboveMinWordCount_ReturnsFalse()
    {
        Assert.False(TranscriptCleanupGuard.ShouldSkipCleanup("please open the settings dialog", Options));
    }

    [Fact]
    public void IsSuspicious_EmptyCleanedText_ReturnsTrue()
    {
        Assert.True(TranscriptCleanupGuard.IsSuspicious("open the pod bay doors", string.Empty, Options));
    }

    [Fact]
    public void IsSuspicious_CleanedTextWithinLengthBudget_ReturnsFalse()
    {
        var raw = "open the pod bay doors please";
        var cleaned = "Open the pod bay doors, please.";

        Assert.False(TranscriptCleanupGuard.IsSuspicious(raw, cleaned, Options));
    }

    [Fact]
    public void IsSuspicious_CleanedTextFarLongerThanRaw_ReturnsTrue()
    {
        var raw = "open the doors";
        var cleaned = new string('x', (int)(raw.Length * Options.MaxLengthRatio) + Options.MaxLengthPadding + 1);

        Assert.True(TranscriptCleanupGuard.IsSuspicious(raw, cleaned, Options));
    }
}
