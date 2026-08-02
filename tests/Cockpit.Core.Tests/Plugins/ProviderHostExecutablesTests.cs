using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>Which PATH command each AI-provider store plugin maps to (AC-510[b] criterion 1) — a CLI provider gets one, a cloud provider gets none.</summary>
public class ProviderHostExecutablesTests
{
    [Theory]
    [InlineData("claude-provider", "claude")]
    [InlineData("cli-agent-provider", "codex")]
    [InlineData("kimi-provider", "kimi")]
    public void CommandFor_KnownCliProvider_ReturnsItsCommand(string pluginId, string expectedCommand) =>
        Assert.Equal(expectedCommand, ProviderHostExecutables.CommandFor(pluginId));

    [Theory]
    [InlineData("gemini-provider")]
    [InlineData("github-models-provider")]
    [InlineData("some-plugin-never-listed")]
    public void CommandFor_CloudOrUnknownProvider_ReturnsNull(string pluginId) =>
        Assert.Null(ProviderHostExecutables.CommandFor(pluginId));
}
