using System.Text.Json;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

// `CodexModelCatalog` (increment 2 step C): drives a `FakeCliSubprocess` through the
// initialize handshake and a model/list reply, proving it parses the offered models and the default without a
// live Codex — and never issues a thread/start (which would cost credits).
public class CodexModelCatalogTests
{
    private static CliAgentConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    [Fact]
    public async Task ListAsync_ParsesTheNonHiddenModels_AndTheDefault_WithoutStartingAThread()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list",
            """{"data":[{"id":"gpt-5.6-terra","isDefault":true},{"id":"gpt-5.6-luna","isDefault":false},{"id":"internal-preview","hidden":true}]}""");
        var listing = await listTask;

        // Hidden models are dropped; the default is the one flagged isDefault.
        Assert.Equal(new[] { "gpt-5.6-terra", "gpt-5.6-luna" }, listing.Ids);
        Assert.Equal("gpt-5.6-terra", listing.DefaultId);
        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"thread/start\""));
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheModelField_WhenAnEntryHasNoId()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"model":"gpt-5.6-luna"}]}""");
        var listing = await listTask;

        Assert.Equal(new[] { "gpt-5.6-luna" }, listing.Ids);
        Assert.Null(listing.DefaultId);
    }

    // AC-1101: each model reports its own reasoning-effort presets — sol/terra offer "ultra", others do not — so
    // the effort control must read this per model rather than assume every model offers the same fixed set.
    [Fact]
    public async Task ListAsync_ParsesEachModelsOwnSupportedReasoningEfforts()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """
            {"data":[
                {"id":"gpt-5.6-sol","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"ultra"}]},
                {"id":"gpt-5.5","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]}
            ]}
            """);
        var listing = await listTask;

        Assert.Equal(new[] { "low", "medium", "ultra" }, listing.ReasoningEffortsFor("gpt-5.6-sol"));
        Assert.Equal(new[] { "low", "high" }, listing.ReasoningEffortsFor("gpt-5.5"));
        // A model the listing has nothing for reports no efforts, rather than borrowing another model's set.
        Assert.Empty(listing.ReasoningEffortsFor("unknown-model"));
    }

    [Fact]
    public async Task ListAsync_IsEmpty_WhenTheReplyCarriesNoModelData()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", "{}");
        var listing = await listTask;

        Assert.Same(CodexModelListing.Empty, listing);
    }

    private static async Task _RespondAsync(FakeCliSubprocess fake, string method, string resultJson)
    {
        var request = await _WaitForRequestAsync(fake, method);
        var id = request.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{{{resultJson}}}}""");
    }

    private static async Task<JsonElement> _WaitForRequestAsync(FakeCliSubprocess fake, string method)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written => written.Contains($"\"method\":\"{method}\""));
            if (line is not null)
            {
                return JsonDocument.Parse(line).RootElement;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No request for method '{method}' was written.");
    }
}
