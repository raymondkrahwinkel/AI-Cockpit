using FluentAssertions;

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

        text.Should().NotContain("super-secret-key");
        text.Should().Contain("***");
        text.Should().Contain("gpt-5-codex");
        text.Should().Contain(@"C:\work");
    }

    [Fact]
    public void ToString_WhenNoApiKeyIsSet_ReportsNullRatherThanEmptyOrAsterisks()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        config.ToString().Should().Contain("ApiKey = null");
    }

    [Fact]
    public void EffectiveOutputFormatArgs_DefaultsToJsonFlag_WhenNotConfigured()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        config.EffectiveOutputFormatArgs.Should().Equal("--json");
    }

    [Fact]
    public void EffectiveExtraArgs_DefaultsToEmpty_WhenNotConfigured()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        config.EffectiveExtraArgs.Should().BeEmpty();
    }

    [Fact]
    public void IsStdinPromptMode_IsFalse_ForTheDefaultArgPromptMode()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work");

        config.IsStdinPromptMode.Should().BeFalse();
    }

    [Fact]
    public void IsStdinPromptMode_IsTrue_WhenConfiguredAsStdin()
    {
        var config = new CliAgentConfig(WorkingDirectory: @"C:\work", PromptMode: "stdin");

        config.IsStdinPromptMode.Should().BeTrue();
    }
}
