using System.Text.Json;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.CliAgentProvider.Tests;

public class CodexModelIntelligenceTests
{
    private static CliAgentConfig _DefaultConfig() => new(WorkingDirectory: Path.GetTempPath());

    public static IEnumerable<object[]> ReadScenarios =>
    [
        [true],
        [false],
    ];

    [Theory]
    [MemberData(nameof(ReadScenarios))]
    public async Task ReadAsync_MapsARecordedReply_OrReturnsUnknownWhenCodexIsUnavailable(bool hasRecordedReply)
    {
        if (!hasRecordedReply)
        {
            await _AssertUnknownWhenUnavailableAsync();
            return;
        }

        var fake = new FakeCliSubprocess();
        var readTask = CodexModelIntelligence.ReadAsync(() => fake, _DefaultConfig(), "codex", null, CancellationToken.None);

        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"id":"gpt-5.6-terra"}]}""");
        await _RespondAsync(fake, "account/rateLimits/read", """{"rateLimits":{"primary":{"usedPercent":60,"resetsAt":1800000000,"windowDurationMins":300},"secondary":{"usedPercent":80,"resetsAt":1800600000,"windowDurationMins":10080}}}""");

        var intelligence = await readTask;

        Assert.Equal("gpt-5.6-terra", Assert.Single(intelligence.Models).PublicModelRef);
        Assert.Equal(new[]
        {
            new PluginRateLimitWindow("5h", 60, DateTimeOffset.FromUnixTimeSeconds(1800000000), 300),
            new PluginRateLimitWindow("7d", 80, DateTimeOffset.FromUnixTimeSeconds(1800600000), 10080),
        }, intelligence.Quota.Select(state => state.Window));
        Assert.DoesNotContain(fake.WrittenLines, line => line.Contains("\"method\":\"thread/start\""));
    }

    private static async Task _AssertUnknownWhenUnavailableAsync()
    {
        var spawnFailure = await CodexModelIntelligence.ReadAsync(
            () => throw new InvalidOperationException("codex is missing"), _DefaultConfig(), "codex", null, CancellationToken.None);

        var fake = new FakeCliSubprocess();
        var loggedOutRead = CodexModelIntelligence.ReadAsync(() => fake, _DefaultConfig(), "codex", null, CancellationToken.None);
        await _RespondAsync(fake, "initialize", "{}");
        await _RespondAsync(fake, "model/list", """{"data":[{"id":"gpt-listed"}]}""");
        await _RespondErrorAsync(fake, "account/rateLimits/read");

        var loggedOut = await loggedOutRead;

        Assert.Empty(spawnFailure.Models);
        Assert.Empty(spawnFailure.Quota);
        Assert.Equal(["gpt-listed"], loggedOut.Models.Select(model => model.Model));
        Assert.Empty(loggedOut.Quota);
    }

    private static async Task _RespondAsync(FakeCliSubprocess fake, string method, string resultJson)
    {
        var request = await _WaitForRequestAsync(fake, method);
        await fake.PushStdoutAsync($$$"""{"id":{{{request.GetProperty("id").GetInt64()}}},"result":{{{resultJson}}}}""");
    }

    private static async Task _RespondErrorAsync(FakeCliSubprocess fake, string method)
    {
        var request = await _WaitForRequestAsync(fake, method);
        await fake.PushStdoutAsync($$$"""{"id":{{{request.GetProperty("id").GetInt64()}}},"error":{"code":-32000,"message":"not logged in"}}""");
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
