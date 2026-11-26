using System.Text.Json;
using Cockpit.Plugins.Abstractions.Profiles;

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
        await _ParsesEachModelsOwnSupportedReasoningEffortsAsync();
    }

    // AC-1101: each model reports its own reasoning-effort presets — sol/terra offer "ultra", others do not — so
    // the effort control must read this per model rather than assume every model offers the same fixed set.
    private static async Task _ParsesEachModelsOwnSupportedReasoningEffortsAsync()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None, readAllPages: true);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """
            {"data":[
                {"id":"gpt-5.6-sol","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"medium"},{"reasoningEffort":"ultra"}]}
            ],"nextCursor":"remaining"}
            """);
        await _RespondAsync(fake, "model/list", """
            {"data":[
                {"id":"gpt-5.5","supportedReasoningEfforts":[{"reasoningEffort":"low"},{"reasoningEffort":"high"}]},
                {"id":"older-model"},
                {"id":"fixed-effort-model","supportedReasoningEfforts":[]}
            ]}
            """, cursor: "remaining");
        var listing = await listTask;

        Assert.Equal(new[] { "low", "medium", "ultra" }, listing.ReasoningEffortsFor("gpt-5.6-sol"));
        Assert.Equal(new[] { "low", "high" }, listing.ReasoningEffortsFor("gpt-5.5"));
        // A model the listing has nothing for reports no efforts, rather than borrowing another model's set.
        Assert.Empty(listing.ReasoningEffortsFor("unknown-model"));

        var catalog = listing.ToProfileCatalog();
        Assert.Equal(PluginCatalogStatus.Known, catalog.Status);
        Assert.Equal(new[] { "gpt-5.6-sol", "gpt-5.5", "older-model", "fixed-effort-model" }, catalog.Models.Select(model => model.Id));
        Assert.Collection(catalog.Models,
            model => Assert.Equal(new[] { "low", "medium", "ultra" }, model.Efforts),
            model => Assert.Equal(new[] { "low", "high" }, model.Efforts),
            model => Assert.Equal(PluginCatalogStatus.Unknown, model.EffortStatus),
            model =>
            {
                Assert.Equal(PluginCatalogStatus.Known, model.EffortStatus);
                Assert.Empty(model.Efforts);
            });
    }

    private static async Task _RespondAsync(FakeCliSubprocess fake, string method, string resultJson, string? cursor = null)
    {
        var request = await _WaitForRequestAsync(fake, method, cursor);
        var id = request.GetProperty("id").GetInt64();
        await fake.PushStdoutAsync($$$"""{"id":{{{id}}},"result":{{{resultJson}}}}""");
    }

    private static async Task<JsonElement> _WaitForRequestAsync(FakeCliSubprocess fake, string method, string? cursor = null)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var line = fake.WrittenLines.LastOrDefault(written => written.Contains($"\"method\":\"{method}\"")
                && (cursor is null || written.Contains($"\"cursor\":\"{cursor}\"")));
            if (line is not null)
            {
                return JsonDocument.Parse(line).RootElement;
            }

            await Task.Delay(10);
        }

        throw new InvalidOperationException($"No request for method '{method}' was written.");
    }
}
