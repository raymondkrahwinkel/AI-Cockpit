using System.Net;
using System.Net.Http.Json;
using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The auto-detect that reuses the memory-breakdown process detection (<see cref="LocalModelServers"/>) to find
/// the running local server and read a model off it — with the configured URL/model as the fallback when
/// auto-detect is off, nothing is running, or the detected server is not actually serving.
/// </summary>
public class LocalLlmEndpointResolverTests
{
    [Fact]
    public async Task ResolveAsync_AutoDetectOff_ReturnsConfigured_WithoutTouchingTheProcessTableOrHttp()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var reader = Substitute.For<IProcessTableReader>();
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = false,
            CleanupBaseUrl = "http://configured:9999",
            CleanupModel = "configured-model",
        });

        endpoint.BaseUrl.Should().Be("http://configured:9999");
        endpoint.Model.Should().Be("configured-model");
        reader.DidNotReceive().Read();
        handler.WasInvoked.Should().BeFalse();
    }

    [Fact]
    public async Task ResolveAsync_LmStudioRunning_UsesItsUrlAndTheConfiguredModelWhenPresent()
    {
        var reader = _ReaderWith("LM Studio");
        var handler = new FakeHttpMessageHandler(_ => _Models("qwen/qwen3-coder-30b", "qwen2.5:3b-instruct"));
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            CleanupBaseUrl = "http://localhost:11434",
            CleanupModel = "qwen2.5:3b-instruct",
        });

        endpoint.BaseUrl.Should().Be("http://localhost:1234");
        endpoint.Model.Should().Be("qwen2.5:3b-instruct");
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredModelAbsent_AutoPicksASmallInstructModel_SkippingEmbeddings()
    {
        var reader = _ReaderWith("LM Studio");
        var handler = new FakeHttpMessageHandler(_ => _Models("text-embedding-nomic", "qwen/qwen3-coder-30b", "phi-3-mini-4k-instruct"));
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            CleanupModel = "qwen2.5:3b-instruct", // not on this server
        });

        endpoint.BaseUrl.Should().Be("http://localhost:1234");
        endpoint.Model.Should().Be("phi-3-mini-4k-instruct");
    }

    [Fact]
    public async Task ResolveAsync_RealLmStudioList_PicksInstructModel_NotTheMoeWhoseIdContains3b()
    {
        // Regression: "qwen/qwen3.6-35b-a3b" contains "a3b" — a naive size match would read it as a 3B and pick
        // this 35B mixture-of-experts. Cleanup should land on the instruction-tuned model instead.
        var reader = _ReaderWith("LM Studio");
        var handler = new FakeHttpMessageHandler(_ => _Models(
            "qwen/qwen3-coder-30b",
            "qwen/qwen3.6-35b-a3b",
            "mistralai/devstral-small-2-2512",
            "bartowski/mistral-nemo-instruct-2407",
            "text-embedding-nomic-embed-text-v1.5"));
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings { AutoDetectLocalLlm = true, CleanupModel = "qwen2.5:3b-instruct" });

        endpoint.BaseUrl.Should().Be("http://localhost:1234");
        endpoint.Model.Should().Be("bartowski/mistral-nemo-instruct-2407");
    }

    [Fact]
    public async Task ResolveAsync_BothRunning_PreferenceForcesTheChosenServer_OverTheHeaviest()
    {
        // LM Studio is heavier, so Auto would land on it; the Ollama preference must win the tie instead.
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns(new List<ProcessRow>
        {
            new(2001, 0, TimeSpan.Zero, 5_000_000, "LM Studio"),
            new(2002, 0, TimeSpan.Zero, 1_000_000, "ollama"),
        });
        var handler = new FakeHttpMessageHandler(_ => _Models("phi-3-mini-4k-instruct"));
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            LocalLlmPreference = LocalLlmPreference.Ollama,
            CleanupModel = "not-on-server",
        });

        endpoint.BaseUrl.Should().Be("http://localhost:11434");
        endpoint.Model.Should().Be("phi-3-mini-4k-instruct");
    }

    [Fact]
    public async Task ResolveAsync_NoServerRunning_FallsBackToConfigured()
    {
        var reader = _ReaderWith(/* no model-server processes */ "explorer", "chrome");
        var handler = new FakeHttpMessageHandler(_ => _Models("should-not-be-read"));
        var resolver = _Create(reader, handler);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            CleanupBaseUrl = "http://configured:11434",
            CleanupModel = "configured-model",
        });

        endpoint.BaseUrl.Should().Be("http://configured:11434");
        endpoint.Model.Should().Be("configured-model");
        handler.WasInvoked.Should().BeFalse();
    }

    private static IProcessTableReader _ReaderWith(params string[] processNames)
    {
        var rows = processNames
            .Select((name, i) => new ProcessRow(1000 + i, 0, TimeSpan.Zero, 1_000_000, name))
            .ToList();
        var reader = Substitute.For<IProcessTableReader>();
        reader.Read().Returns(rows);
        return reader;
    }

    private static HttpResponseMessage _Models(params string[] ids) => new(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new { data = ids.Select(id => new { id }).ToArray() }),
    };

    private static LocalLlmEndpointResolver _Create(IProcessTableReader reader, HttpMessageHandler handler) =>
        new(reader, new HttpClient(handler), NullLogger<LocalLlmEndpointResolver>.Instance);
}
