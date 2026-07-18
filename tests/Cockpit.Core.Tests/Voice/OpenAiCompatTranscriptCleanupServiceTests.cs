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
