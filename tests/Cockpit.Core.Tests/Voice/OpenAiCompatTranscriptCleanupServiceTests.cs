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
/// The transcript cleanup safety net (ported from WisperFlow's <c>cleanup.py</c>): an unreachable/erroring
/// local LLM and suspicious-looking output both fall back to the raw transcript instead of surfacing an
/// error or risking a hallucinated result — verified against the real HTTP call (the OpenAI-compatible
/// <c>/v1/chat/completions</c> that Ollama and LM Studio both serve) via a fake handler, with
/// <see cref="TranscriptCleanupGuardTests"/> covering the pure decision logic in isolation.
/// </summary>
public class OpenAiCompatTranscriptCleanupServiceTests
{
    private static readonly VoiceSettings Settings = new() { CleanupBaseUrl = "http://local.llm", CleanupModel = "qwen2.5:3b-instruct" };

    [Fact]
    public async Task CleanupAsync_ServerUnreachable_ReturnsRawTranscript()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("open the settings dialog for me");
    }

    [Fact]
    public async Task CleanupAsync_ServerReturnsError_ReturnsRawTranscript()
    {
        // LM Studio answering a path it does not serve (or any non-2xx) must fall back, not throw.
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("open the settings dialog for me");
    }

    [Fact]
    public async Task CleanupAsync_SuspiciouslyLongOutput_ReturnsRawTranscript()
    {
        var raw = "open the settings dialog";
        var handler = new FakeHttpMessageHandler(_ => _ChatResponse(new string('x', 500)));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync(raw);

        result.Should().Be(raw);
    }

    [Fact]
    public async Task CleanupAsync_PlausibleOutput_ReturnsCleanedTranscript()
    {
        var handler = new FakeHttpMessageHandler(_ => _ChatResponse("Open the settings dialog for me."));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("Open the settings dialog for me.");
    }

    [Fact]
    public async Task CleanupAsync_HitsTheOpenAiCompatChatCompletionsPath()
    {
        string? requestedUri = null;
        var handler = new FakeHttpMessageHandler(request =>
        {
            requestedUri = request.RequestUri?.ToString();
            return _ChatResponse("Open the settings dialog for me.");
        });
        var service = _CreateService(handler);

        await service.CleanupAsync("open the settings dialog for me");

        requestedUri.Should().Be("http://local.llm/v1/chat/completions");
    }

    [Fact]
    public async Task CleanupAsync_TooFewWords_SkipsTheHttpCallEntirely_AndReturnsRaw()
    {
        var handler = new FakeHttpMessageHandler(_ => _ChatResponse("should never be reached"));
        var service = _CreateService(handler);

        var result = await service.CleanupAsync("no");

        result.Should().Be("no");
        handler.WasInvoked.Should().BeFalse();
    }

    private static HttpResponseMessage _ChatResponse(string content) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { choices = new[] { new { message = new { role = "assistant", content } } } }),
    };

    private static OpenAiCompatTranscriptCleanupService _CreateService(HttpMessageHandler handler)
    {
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings);

        // The endpoint/model choice is the resolver's job (covered by LocalLlmEndpointResolverTests); here it is
        // pinned so these tests exercise only the HTTP call, its response parsing, and the fallback guards.
        var resolver = Substitute.For<ILocalLlmEndpointResolver>();
        resolver.ResolveAsync(Arg.Any<VoiceSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LocalLlmEndpoint("http://local.llm", "qwen2.5:3b-instruct")));

        return new OpenAiCompatTranscriptCleanupService(
            new HttpClient(handler),
            settingsStore,
            resolver,
            NullLogger<OpenAiCompatTranscriptCleanupService>.Instance);
    }
}
