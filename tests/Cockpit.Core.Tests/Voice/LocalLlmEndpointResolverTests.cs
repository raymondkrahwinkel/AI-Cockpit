using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Diagnostics;
using Cockpit.Core.Voice;
using Cockpit.Infrastructure.Voice;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Voice;

/// <summary>
/// The auto-detect that reuses the memory-breakdown process detection (<see cref="LocalModelServers"/>) to find
/// the running local server and read a model off it (through the shared <see cref="IModelCatalog"/>) — with the
/// configured URL/model as the fallback when auto-detect is off, nothing is running, or the detected server is
/// not actually serving.
/// </summary>
public class LocalLlmEndpointResolverTests
{
    [Fact]
    public async Task ResolveAsync_AutoDetectOff_ReturnsConfigured_WithoutTouchingTheProcessTableOrCatalog()
    {
        var catalog = Substitute.For<IModelCatalog>();
        var reader = Substitute.For<IProcessTableReader>();
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = false,
            VoiceLlmBaseUrl = "http://configured:9999",
            VoiceLlmModel = "configured-model",
        });

        endpoint.BaseUrl.Should().Be("http://configured:9999");
        endpoint.Model.Should().Be("configured-model");
        reader.DidNotReceive().Read();
        await catalog.DidNotReceiveWithAnyArgs().ListModelsAsync(default!, default, default);
    }

    [Fact]
    public async Task ResolveAsync_AutoModelInManualMode_PicksFromTheConfiguredServer()
    {
        // Manual server, but "Auto" model (empty): the resolver reads the configured server's list and picks one.
        var reader = Substitute.For<IProcessTableReader>();
        var catalog = _CatalogWith("text-embedding-nomic", "phi-3-mini-4k-instruct");
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = false,
            VoiceLlmBaseUrl = "http://configured:1234",
            VoiceLlmModel = "",
        });

        endpoint.BaseUrl.Should().Be("http://configured:1234");
        endpoint.Model.Should().Be("phi-3-mini-4k-instruct");
    }

    [Fact]
    public async Task ResolveAsync_LmStudioRunning_UsesItsUrlAndTheConfiguredModelWhenPresent()
    {
        var reader = _ReaderWith("LM Studio");
        var catalog = _CatalogWith("qwen/qwen3-coder-30b", "qwen2.5:3b-instruct");
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            VoiceLlmBaseUrl = "http://localhost:11434",
            VoiceLlmModel = "qwen2.5:3b-instruct",
        });

        endpoint.BaseUrl.Should().Be("http://localhost:1234");
        endpoint.Model.Should().Be("qwen2.5:3b-instruct");
    }

    [Fact]
    public async Task ResolveAsync_ConfiguredModelAbsent_AutoPicksASmallInstructModel_SkippingEmbeddings()
    {
        var reader = _ReaderWith("LM Studio");
        var catalog = _CatalogWith("text-embedding-nomic", "qwen/qwen3-coder-30b", "phi-3-mini-4k-instruct");
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            VoiceLlmModel = "qwen2.5:3b-instruct", // not on this server
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
        var catalog = _CatalogWith(
            "qwen/qwen3-coder-30b",
            "qwen/qwen3.6-35b-a3b",
            "mistralai/devstral-small-2-2512",
            "bartowski/mistral-nemo-instruct-2407",
            "text-embedding-nomic-embed-text-v1.5");
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings { AutoDetectLocalLlm = true, VoiceLlmModel = "qwen2.5:3b-instruct" });

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
        var catalog = _CatalogWith("phi-3-mini-4k-instruct");
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            LocalLlmPreference = LocalLlmPreference.Ollama,
            VoiceLlmModel = "not-on-server",
        });

        endpoint.BaseUrl.Should().Be("http://localhost:11434");
        endpoint.Model.Should().Be("phi-3-mini-4k-instruct");
    }

    [Fact]
    public async Task ResolveAsync_NoServerRunning_FallsBackToConfigured()
    {
        var reader = _ReaderWith(/* no model-server processes */ "explorer", "chrome");
        var catalog = Substitute.For<IModelCatalog>();
        var resolver = _Create(reader, catalog);

        var endpoint = await resolver.ResolveAsync(new VoiceSettings
        {
            AutoDetectLocalLlm = true,
            VoiceLlmBaseUrl = "http://configured:11434",
            VoiceLlmModel = "configured-model",
        });

        endpoint.BaseUrl.Should().Be("http://configured:11434");
        endpoint.Model.Should().Be("configured-model");
        await catalog.DidNotReceiveWithAnyArgs().ListModelsAsync(default!, default, default);
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

    private static IModelCatalog _CatalogWith(params string[] ids)
    {
        var catalog = Substitute.For<IModelCatalog>();
        catalog.ListModelsAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)ids.ToList());
        return catalog;
    }

    private static LocalLlmEndpointResolver _Create(IProcessTableReader reader, IModelCatalog catalog) =>
        new(reader, catalog, NullLogger<LocalLlmEndpointResolver>.Instance);
}
