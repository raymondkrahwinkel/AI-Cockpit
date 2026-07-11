using Cockpit.Core.Mcp;
using FluentAssertions;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// <see cref="McpServerRegistryFilter.ApplySessionSelection"/>: the per-session MCP-server selection (#44)
/// narrows the registry to the given names, and a <see langword="null"/> selection is a no-op pass-through.
/// </summary>
public class McpServerRegistryFilterTests
{
    private static readonly McpServerConfig ServerA = new() { Name = "server-a", Command = "npx" };
    private static readonly McpServerConfig ServerB = new() { Name = "server-b", Command = "npx" };

    [Fact]
    public void ApplySessionSelection_WithNullSelection_ReturnsTheFullRegistry()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], enabledServerNames: null);

        result.Should().Equal(ServerA, ServerB);
    }

    [Fact]
    public void ApplySessionSelection_WithASelection_KeepsOnlyTheNamedServers()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], new HashSet<string> { "server-a" });

        result.Should().ContainSingle().Which.Should().Be(ServerA);
    }

    [Fact]
    public void ApplySessionSelection_WithAnEmptySelection_DropsEveryEnabledRegistryServer()
    {
        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, ServerB], new HashSet<string>());

        result.Should().BeEmpty();
    }

    [Fact]
    public void ApplySessionSelection_KeepsAnAlreadyDisabledServer_EvenWhenNotInTheSelection()
    {
        var disabled = ServerB with { Enabled = false };

        var result = McpServerRegistryFilter.ApplySessionSelection([ServerA, disabled], new HashSet<string> { "server-a" });

        // The checklist only ever offers enabled registry servers, so a disabled one — e.g. one that
        // deliberately overrides and suppresses a local-model built-in default (#26) — was never a
        // checkbox the operator could uncheck, and must keep passing through untouched.
        result.Should().Equal(ServerA, disabled);
    }
}
