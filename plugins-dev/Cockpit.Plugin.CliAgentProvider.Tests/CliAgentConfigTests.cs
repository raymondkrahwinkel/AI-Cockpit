namespace Cockpit.Plugin.CliAgentProvider.Tests;

/// <summary>
/// <see cref="CliAgentConfig"/>'s <c>ToString()</c> override (#45 fase B1, mirrors the Gemini/OpenAI plugin's
/// own <c>OpenAiCompatConfigTests</c>): a plain <c>record</c>'s auto-generated <c>ToString()</c> would print
/// <see cref="CliAgentConfig.ApiKey"/> in the clear — a leak surface anywhere this config lands in a log line
/// or exception message.
/// </summary>
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

    [Fact]
    public void EffectiveOutputFormatArgs_DefaultsToJsonFlag_WhenNotConfigured()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        Assert.Equal(new[] { "--json" }, config.EffectiveOutputFormatArgs);
    }

    [Fact]
    public void EffectiveExtraArgs_DefaultsToEmpty_WhenNotConfigured()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        Assert.Empty(config.EffectiveExtraArgs);
    }

    [Fact]
    public void IsStdinPromptMode_IsFalse_ForTheDefaultArgPromptMode()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        Assert.False(config.IsStdinPromptMode);
    }

    [Fact]
    public void IsStdinPromptMode_IsTrue_WhenConfiguredAsStdin()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work", PromptMode: "stdin");

        Assert.True(config.IsStdinPromptMode);
    }
}
