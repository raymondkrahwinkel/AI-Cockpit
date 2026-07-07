using System.Net;
using System.Net.Http.Json;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The Ollama cleanup safety net (ported from WisperFlow's <c>cleanup.py</c>): unreachable/erroring
/// Ollama and suspicious-looking output both fall back to the raw transcript instead of surfacing an
/// error or risking a hallucinated result — verified against the real HTTP call via a fake handler,
/// with <see cref="TranscriptCleanupGuardTests"/> covering the pure decision logic in isolation.
/// </summary>
public class OllamaTranscriptCleanupServiceTests
{
    private static readonly VoiceSettings Settings = new() { OllamaBaseUrl = "http://ollama.local", CleanupModel = "qwen2.5:3b-instruct" };

    [Fact]
    public async Task CleanupAsync_OllamaUnreachable_ReturnsRawTranscript()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("open the settings dialog for me");
    }

    [Fact]
    public async Task CleanupAsync_SuspiciouslyLongOutput_ReturnsRawTranscript()
    {
        var raw = "open the settings dialog";
        var handler = new FakeHttpMessageHandler(_ => _JsonResponse(new { response = new string('x', 500) }));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync(raw);

        result.Should().Be(raw);
    }

    [Fact]
    public async Task CleanupAsync_PlausibleOutput_ReturnsCleanedTranscript()
    {
        var handler = new FakeHttpMessageHandler(_ => _JsonResponse(new { response = "Open the settings dialog for me." }));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("Open the settings dialog for me.");
    }

    [Fact]
    public async Task CleanupAsync_TooFewWords_SkipsTheHttpCallEntirely_AndReturnsRaw()
    {
        var handler = new FakeHttpMessageHandler(_ => _JsonResponse(new { response = "should never be reached" }));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("no");

        result.Should().Be("no");
        handler.WasInvoked.Should().BeFalse();
    }

    private static HttpResponseMessage _JsonResponse(object body) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(body),
    };

    private static OllamaTranscriptCleanupService _CreateService(HttpMessageHandler handler)
    {
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings);

        return new OllamaTranscriptCleanupService(
            new HttpClient(handler),
            settingsStore,
            NullLogger<OllamaTranscriptCleanupService>.Instance);
    }
}
