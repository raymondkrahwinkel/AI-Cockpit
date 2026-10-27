using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.ModelIntelligence;

namespace Cockpit.Core.Tests.ModelIntelligence;

public sealed class LocalModelIntelligenceTests
{
    [Fact]
    public async Task GetLmStudioModelsAsync_LocalRowBeatsAPublicMatch()
    {
        var intelligence = new LocalModelIntelligence(new _Catalog("qwen-local"));
        var row = new LocalModelEntry("qwen-local", "operator tier", ["coding"], "local note");

        var model = Assert.Single(await intelligence.GetLmStudioModelsAsync("http://localhost:1234", null, [row]));

        Assert.Equal(0m, model.Price.PromptPerMillion);
        Assert.Equal(0m, model.Price.CompletionPerMillion);
        Assert.Equal("operator tier", model.Tier);
        Assert.Equal(["coding"], model.Strengths);
        Assert.Equal("local note", model.Notes);
    }

    [Fact]
    public async Task GetLmStudioModelsAsync_UsesZeroRatherThanUnknownForFreeModels()
    {
        var intelligence = new LocalModelIntelligence(new _Catalog("qwen-local"));

        var model = Assert.Single(await intelligence.GetLmStudioModelsAsync("http://localhost:1234", null, []));

        Assert.Equal(new ModelPrice(0m, 0m), model.Price);
        Assert.NotEqual(new ModelPrice(null, null), model.Price);
        Assert.Empty(model.Quota);
    }

    private sealed class _Catalog(params string[] modelIds) : IModelCatalog
    {
        public Task<IReadOnlyList<string>> ListModelsAsync(string baseUrl, string? apiKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(modelIds);

        public Task<bool> ProbeAsync(string baseUrl, string? apiKey, TimeSpan timeout) => Task.FromResult(true);
    }
}
