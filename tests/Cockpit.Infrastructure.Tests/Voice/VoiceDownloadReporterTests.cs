using System.Text;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;

namespace Cockpit.Infrastructure.Tests.Voice;

/// <summary>
/// What the operator reads while a first dictation waits on gigabytes. The rule under all of it: never claim
/// to know something we do not — a bar parked at an invented percentage is the same class of lie as the
/// spinner that used to say "Transcribing…" through the whole download.
/// </summary>
public class VoiceDownloadReporterTests
{
    [Fact]
    public async Task CopyAsync_WithAKnownTotal_ReportsAFractionAndReachesOne()
    {
        var steps = new List<VoicePreparationProgress>();
        var source = new MemoryStream(new byte[4 * 1024 * 1024]);

        await VoiceDownloadReporter.CopyAsync(
            source, Stream.Null, "Downloading Vulkan runtime", source.Length, _Collect(steps), CancellationToken.None);

        steps.Should().NotBeEmpty();
        steps[^1].Fraction.Should().Be(1);
        steps[^1].Description.Should().Contain("Downloading Vulkan runtime").And.Contain("100%");
    }

    /// <summary>
    /// The ggml downloader hands back a stream with no length. Counting megabytes is honest; a percentage
    /// there would be invented, and an invented one that sticks is worse than a number that only climbs.
    /// </summary>
    [Fact]
    public async Task CopyAsync_WithNoKnownTotal_CountsMegabytesAndOffersNoFraction()
    {
        var steps = new List<VoicePreparationProgress>();
        var source = new MemoryStream(new byte[3 * 1024 * 1024]);

        await VoiceDownloadReporter.CopyAsync(
            source, Stream.Null, "Downloading speech model", totalBytes: null, _Collect(steps), CancellationToken.None);

        steps.Should().OnlyContain(step => step.Fraction == null);
        steps[^1].Description.Should().Be("Downloading speech model — 3 MB");
    }

    /// <summary>A zero Content-Length is not a total; dividing by it would be worse than counting bytes.</summary>
    [Fact]
    public async Task CopyAsync_WithAZeroTotal_FallsBackToCountingRatherThanDividingByZero()
    {
        var steps = new List<VoicePreparationProgress>();

        await VoiceDownloadReporter.CopyAsync(
            new MemoryStream([1, 2, 3]), Stream.Null, "Downloading", totalBytes: 0, _Collect(steps), CancellationToken.None);

        steps.Should().OnlyContain(step => step.Fraction == null);
    }

    [Fact]
    public async Task CopyAsync_CopiesEveryByteItReportsOn()
    {
        var payload = Encoding.UTF8.GetBytes("the runtime, all of it");
        var target = new MemoryStream();

        await VoiceDownloadReporter.CopyAsync(
            new MemoryStream(payload), target, "Downloading", payload.Length, _Collect([]), CancellationToken.None);

        target.ToArray().Should().Equal(payload);
    }

    /// <summary>Without a progress sink this is a plain copy — the caller that wants no narration pays nothing for it.</summary>
    [Fact]
    public async Task CopyAsync_WithoutAProgressSink_StillCopies()
    {
        var payload = Encoding.UTF8.GetBytes("quietly");
        var target = new MemoryStream();

        await VoiceDownloadReporter.CopyAsync(
            new MemoryStream(payload), target, "Downloading", payload.Length, progress: null, CancellationToken.None);

        target.ToArray().Should().Equal(payload);
    }

    private static IProgress<VoicePreparationProgress> _Collect(List<VoicePreparationProgress> into) =>
        new ImmediateProgress<VoicePreparationProgress>(into.Add);
}
