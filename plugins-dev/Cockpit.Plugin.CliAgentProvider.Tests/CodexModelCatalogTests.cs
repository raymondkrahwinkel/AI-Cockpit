using System.Text.Json;
using FluentAssertions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CodexModelCatalog"/> (increment 2 step C): drives a <see cref="FakeCliSubprocess"/> through the
/// initialize handshake and a model/list reply, proving it parses the offered models and the default without a
/// live Codex — and never issues a thread/start (which would cost credits).
/// </summary>
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
        listing.Ids.Should().Equal("gpt-5.6-terra", "gpt-5.6-luna");
        listing.DefaultId.Should().Be("gpt-5.6-terra");
        fake.WrittenLines.Should().NotContain(line => line.Contains("\"method\":\"thread/start\""));
    }

    [Fact]
    public async Task ListAsync_FallsBackToTheModelField_WhenAnEntryHasNoId()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"model":"gpt-5.6-luna"}]}""");
        var listing = await listTask;

        listing.Ids.Should().Equal("gpt-5.6-luna");
        listing.DefaultId.Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_IsEmpty_WhenTheReplyCarriesNoModelData()
    {
        var fake = new FakeCliSubprocess();
        var listTask = CodexModelCatalog.ListAsync(() => fake, _DefaultConfig(), "codex", CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", "{}");
        var listing = await listTask;

        listing.Should().BeSameAs(CodexModelListing.Empty);
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
