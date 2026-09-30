using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Mcp;

/// <summary>
/// AC-134: the pre-flight MCP tool-token estimate. <see cref="McpToolTokenMath"/> is the chars/≈4 heuristic, and
/// <see cref="McpToolTokenEstimator"/> connects a server once, serialises its tools, counts, and caches — with an
/// unavailable result for a server that could not be enumerated.
/// </summary>
public class McpToolTokenEstimatorTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void EstimateTokens_CountsCharactersAtTheRatio_RoundingUp(int _)
    {
        Assert.Equal(0, McpToolTokenMath.EstimateTokens([]));
        Assert.Equal(0, McpToolTokenMath.EstimateTokens([""]));
        Assert.Equal(1, McpToolTokenMath.EstimateTokens(["abcd"]));          // 4 / 4
        Assert.Equal(2, McpToolTokenMath.EstimateTokens(["abcde"]));         // 5 / 4 → ceil
        Assert.Equal(2, McpToolTokenMath.EstimateTokens(["abcd", "abcd"])); // 8 / 4
    }

    [Fact]
    public async Task EstimateAsync_EnumeratesTheServer_CountsItsToolsAndTheirTokens()
    {
        var provider = _ProviderReturning("youtrack", _Tool("search", "Find issues"), _Tool("create", "Open an issue"));
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        var estimate = await estimator.EstimateAsync("youtrack");

        Assert.True(estimate.Available);
        Assert.Equal("youtrack", estimate.ServerName);
        Assert.Equal(2, estimate.ToolCount);
        Assert.True(estimate.EstimatedTokens > 0);
    }

    private static IMcpToolProvider _ProviderReturning(string serverName, params AIFunction[] tools)
    {
        var provider = Substitute.For<IMcpToolProvider>();
        provider.EnumerateServerToolsAsync(serverName, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(tools);
        return provider;
    }

    private static AIFunction _Tool(string name, string description) =>
        AIFunctionFactory.Create((string query) => query, name, description);
}
