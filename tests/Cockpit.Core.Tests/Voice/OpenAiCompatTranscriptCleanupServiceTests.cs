using System.ClientModel;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Sessions;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The transcript cleanup safety net (ported from WisperFlow's <c>cleanup.py</c>): an unreachable/erroring
/// local LLM and suspicious-looking output both fall back to the raw transcript instead of surfacing an error
/// or risking a hallucinated result — verified against a fake <see cref="IChatClient"/> built by the shared
/// <see cref="IChatClientFactory"/> (the OpenAI SDK path Ollama and LM Studio both serve), with
/// <see cref="TranscriptCleanupGuardTests"/> covering the pure decision logic in isolation.
/// </summary>
public class OpenAiCompatTranscriptCleanupServiceTests
{
    private static readonly VoiceSettings Settings = new() { VoiceLlmBaseUrl = "http://local.llm", VoiceLlmModel = "qwen2.5:3b-instruct" };

    [Fact]
    public async Task CleanupAsync_ServerUnreachable_ReturnsRawTranscript()
    {
        var chatClient = _Throwing(new HttpRequestException("connection refused"));
        var service = _CreateService(chatClient, out _);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("open the settings dialog for me");
    }

    [Fact]
    public async Task CleanupAsync_ServerReturnsError_ReturnsRawTranscript()
    {
        // LM Studio answering a path it does not serve surfaces as a ClientResultException from the SDK; it must
        // fall back, not throw.
        var chatClient = _Throwing(new ClientResultException("404 Not Found"));
        var service = _CreateService(chatClient, out _);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("open the settings dialog for me");
    }

    [Fact]
    public async Task CleanupAsync_SuspiciouslyLongOutput_ReturnsRawTranscript()
    {
        var raw = "open the settings dialog";
        var service = _CreateService(_Chat(new string('x', 500)), out _);

        var result = await service.CleanupAsync(raw);

        result.Should().Be(raw);
    }

    [Fact]
    public async Task CleanupAsync_PlausibleOutput_ReturnsCleanedTranscript()
    {
        var service = _CreateService(_Chat("Open the settings dialog for me."), out _);

        var result = await service.CleanupAsync("open the settings dialog for me");

        result.Should().Be("Open the settings dialog for me.");
    }

    [Fact]
    public async Task CleanupAsync_BuildsTheChatClientForTheResolvedEndpoint()
    {
        var service = _CreateService(_Chat("Open the settings dialog for me."), out var factory);

        await service.CleanupAsync("open the settings dialog for me");

        factory.Received(1).CreateForEndpoint("http://local.llm", "qwen2.5:3b-instruct", Arg.Any<string?>());
    }

    [Fact]
    public async Task CleanupAsync_TooFewWords_SkipsTheModelCallEntirely_AndReturnsRaw()
    {
        var service = _CreateService(_Chat("should never be reached"), out var factory);

        var result = await service.CleanupAsync("no");

        result.Should().Be("no");
        factory.DidNotReceiveWithAnyArgs().CreateForEndpoint(default!, default!, default);
    }

    [Fact]
    public async Task NaturalizeForSpeechAsync_CapsTheOutputTokens_SoARunawayGenerationCannotPinTheServer()
    {
        ChatOptions? captured = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Do<ChatOptions?>(options => captured = options), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Spoken.")));
        var service = _CreateService(chatClient, out _);

        await service.NaturalizeForSpeechAsync("Here is a reply the operator asked to be read aloud naturally.");

        captured.Should().NotBeNull();
        captured!.MaxOutputTokens.Should().NotBeNull().And.BeGreaterThan(0);
    }

    [Fact]
    public async Task NaturalizeForSpeechAsync_CallTimesOut_ReturnsTheOriginalText()
    {
        // A timeout fires the service's own linked token, surfacing as OperationCanceledException — it must fall back
        // to the original text, not throw. (Before the hardening the catch only named TaskCanceledException, so a
        // bare OperationCanceledException escaped.)
        var service = _CreateService(_Throwing(new OperationCanceledException()), out _);

        var result = await service.NaturalizeForSpeechAsync("the original reply");

        result.Should().Be("the original reply");
    }

    [Fact]
    public async Task NaturalizeForSpeechAsync_CallerCancels_Propagates_RatherThanPassingOffTheOriginal()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var service = _CreateService(_Chat("unused"), out _);

        var act = () => service.NaturalizeForSpeechAsync("the original reply", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ConcurrentCalls_AreSerialized_SoOnlyOneEverHitsTheServerAtATime()
    {
        var release = new TaskCompletionSource<ChatResponse>();
        var inFlight = 0;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref inFlight);
                return release.Task;
            });
        var service = _CreateService(chatClient, out _);

        var first = service.NaturalizeForSpeechAsync("first reply");
        var second = service.NaturalizeForSpeechAsync("second reply");
        await Task.Delay(50);

        // The single-flight gate holds the second call before it reaches the server; without it both would be in flight.
        inFlight.Should().Be(1);

        release.SetResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Spoken.")));
        await Task.WhenAll(first, second);

        inFlight.Should().Be(2);
    }

    [Fact]
    public async Task AcknowledgeForSpeechAsync_ReturnsTheGeneratedLine()
    {
        var service = _CreateService(_Chat("Let me take a look."), out _);

        var result = await service.AcknowledgeForSpeechAsync("fix the failing build");

        result.Should().Be("Let me take a look.");
    }

    [Fact]
    public async Task AcknowledgeForSpeechAsync_ServerUnavailable_ReturnsEmpty_SoTheCallerFallsBackToAPreset()
    {
        var service = _CreateService(_Throwing(new HttpRequestException("connection refused")), out _);

        var result = await service.AcknowledgeForSpeechAsync("fix the failing build");

        result.Should().BeEmpty();
    }

    private static IChatClient _Chat(string content)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, content)));
        return chatClient;
    }

    private static IChatClient _Throwing(Exception exception)
    {
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(exception);
        return chatClient;
    }

    private static OpenAiCompatTranscriptCleanupService _CreateService(IChatClient chatClient, out IChatClientFactory factory)
    {
        var settingsStore = Substitute.For<IVoiceSettingsStore>();
        settingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Settings);

        // The endpoint/model choice is the resolver's job (covered by LocalLlmEndpointResolverTests); here it is
        // pinned so these tests exercise only the chat call, its response handling, and the fallback guards.
        var resolver = Substitute.For<ILocalLlmEndpointResolver>();
        resolver.ResolveAsync(Arg.Any<VoiceSettings>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new LocalLlmEndpoint("http://local.llm", "qwen2.5:3b-instruct")));

        factory = Substitute.For<IChatClientFactory>();
        factory.CreateForEndpoint(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(chatClient);

        return new OpenAiCompatTranscriptCleanupService(
            factory,
            settingsStore,
            resolver,
            NullLogger<OpenAiCompatTranscriptCleanupService>.Instance);
    }
}
