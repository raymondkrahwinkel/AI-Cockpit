using Cockpit.Core.Mcp;
using Cockpit.Infrastructure.Mcp;
using FluentAssertions;
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
        McpToolTokenMath.EstimateTokens([]).Should().Be(0);
        McpToolTokenMath.EstimateTokens([""]).Should().Be(0);
        McpToolTokenMath.EstimateTokens(["abcd"]).Should().Be(1);          // 4 / 4
        McpToolTokenMath.EstimateTokens(["abcde"]).Should().Be(2);         // 5 / 4 → ceil
        McpToolTokenMath.EstimateTokens(["abcd", "abcd"]).Should().Be(2);  // 8 / 4
    }

    [Fact]
    public async Task EstimateAsync_EnumeratesTheServer_CountsItsToolsAndTheirTokens()
    {
        var provider = _ProviderReturning("youtrack", _Tool("search", "Find issues"), _Tool("create", "Open an issue"));
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        var estimate = await estimator.EstimateAsync("youtrack");

        estimate.Available.Should().BeTrue();
        estimate.ServerName.Should().Be("youtrack");
        estimate.ToolCount.Should().Be(2);
        estimate.EstimatedTokens.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task EstimateAsync_CachesTheResult_AndOnlyReEnumeratesOnRefresh()
    {
        var provider = _ProviderReturning("docker", _Tool("ps", "List containers"));
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        await estimator.EstimateAsync("docker");
        await estimator.EstimateAsync("docker");
        await provider.Received(1).EnumerateServerToolsAsync("docker", Arg.Any<CancellationToken>());

        await estimator.EstimateAsync("docker", refresh: true);
        await provider.Received(2).EnumerateServerToolsAsync("docker", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EstimateAsync_ConcurrentCallsForTheSameServer_EnumerateItOnlyOnce()
    {
        // Single-flight: several dialogs/profiles counting the same server at once must share one enumeration, not
        // each spawn it before the first result lands (AC-134 review).
        var gate = new TaskCompletionSource<IReadOnlyList<AIFunction>?>();
        var provider = Substitute.For<IMcpToolProvider>();
        provider.EnumerateServerToolsAsync("git", Arg.Any<CancellationToken>()).Returns(gate.Task);
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        var first = estimator.EstimateAsync("git");
        var second = estimator.EstimateAsync("git");
        gate.SetResult([_Tool("log", "Show history")]);
        await Task.WhenAll(first, second);

        await provider.Received(1).EnumerateServerToolsAsync("git", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EstimateAsync_WhenTheServerCannotBeEnumerated_IsUnavailable()
    {
        var provider = Substitute.For<IMcpToolProvider>();
        provider.EnumerateServerToolsAsync("needs-auth", Arg.Any<CancellationToken>()).Returns((IReadOnlyList<AIFunction>?)null);
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        var estimate = await estimator.EstimateAsync("needs-auth");

        estimate.Available.Should().BeFalse();
        estimate.ToolCount.Should().Be(0);
        estimate.EstimatedTokens.Should().Be(0);
    }

    [Fact]
    public async Task EstimateAsync_WhenEnumeratingThrows_IsUnavailable_NotAnException()
    {
        var provider = Substitute.For<IMcpToolProvider>();
        provider.EnumerateServerToolsAsync("broken", Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<AIFunction>?>>(_ => throw new InvalidOperationException("boom"));
        var estimator = new McpToolTokenEstimator(provider, NullLogger<McpToolTokenEstimator>.Instance);

        var estimate = await estimator.EstimateAsync("broken");

        estimate.Available.Should().BeFalse();
    }

    private static IMcpToolProvider _ProviderReturning(string serverName, params AIFunction[] tools)
    {
        var provider = Substitute.For<IMcpToolProvider>();
        provider.EnumerateServerToolsAsync(serverName, Arg.Any<CancellationToken>()).Returns(tools);
        return provider;
    }

    private static AIFunction _Tool(string name, string description) =>
        AIFunctionFactory.Create((string query) => query, name, description);
}
