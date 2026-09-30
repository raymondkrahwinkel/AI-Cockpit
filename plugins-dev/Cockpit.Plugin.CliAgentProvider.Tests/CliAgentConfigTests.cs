namespace Cockpit.Plugin.CliAgentProvider.Tests;

// A plain `record`'s auto-generated `ToString()` would print `CliAgentConfig.ApiKey` in the
// clear — a leak surface anywhere this config lands in a log line or exception message.
public class CliAgentConfigTests
{
    [Fact]
    public void ToString_RedactsTheApiKey()
    {
        var config = new CliAgentConfig(Command: "codex", Model: "gpt-5-codex", WorkingDirectory: @"C:\work", ApiKey: "super-secret-key");

        var text = config.ToString();

        Assert.DoesNotContain("super-secret-key", text);
        Assert.Contains("***", text);
        Assert.Contains("gpt-5-codex", text);
        Assert.Contains(@"C:\work", text);
    }

    [Fact]
    public void ToString_WhenNoApiKeyIsSet_ReportsNullRatherThanEmptyOrAsterisks()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        Assert.Contains("ApiKey = null", config.ToString());
    }
}
