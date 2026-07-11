using FluentAssertions;

namespace Cockpit.Plugin.YouTrack.Tests;

/// <summary>
/// <see cref="YouTrackMcpRegistration"/> (#60): the pure per-instance mapping from a configured
/// <see cref="YouTrackInstance"/> to the JetBrains remote MCP-server contribution — endpoint derivation
/// ("/api" -&gt; "/mcp", case-insensitive, trailing-slash tolerant) and skipping an instance that isn't fully
/// configured yet.
/// </summary>
public class YouTrackMcpRegistrationTests
{
    [Theory]
    [InlineData("https://x.youtrack.cloud/api", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://x.youtrack.cloud/API", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://x.youtrack.cloud/api/", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://x.youtrack.cloud/Api/", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://x.youtrack.cloud", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://x.youtrack.cloud/", "https://x.youtrack.cloud/mcp")]
    [InlineData("https://myjetbrains.com/youtrack/api", "https://myjetbrains.com/youtrack/mcp")]
    public void DeriveMcpEndpoint_MapsTheApiBaseUrlToTheMcpEndpoint(string instanceBaseUrl, string expected)
    {
        YouTrackMcpRegistration.DeriveMcpEndpoint(instanceBaseUrl).Should().Be(expected);
    }

    [Fact]
    public void BuildContributions_NoInstances_ReturnsNothing()
    {
        YouTrackMcpRegistration.BuildContributions([]).Should().BeEmpty();
    }

    [Fact]
    public void BuildContributions_InstanceMissingUrlOrToken_IsSkipped()
    {
        var instances = new List<YouTrackInstance>
        {
            new("No URL", string.Empty, "token", string.Empty),
            new("No token", "https://x.youtrack.cloud/api", string.Empty, string.Empty),
            new("Blank token", "https://x.youtrack.cloud/api", "   ", string.Empty),
        };

        YouTrackMcpRegistration.BuildContributions(instances).Should().BeEmpty();
    }

    [Fact]
    public void BuildContributions_FullyConfiguredInstance_YieldsOneNamedHttpContribution()
    {
        var instances = new List<YouTrackInstance>
        {
            new("Prod", "https://x.youtrack.cloud/api", "secret-token", "PROJ"),
        };

        var contributions = YouTrackMcpRegistration.BuildContributions(instances);

        contributions.Should().ContainSingle();
        contributions[0].Name.Should().Be("YouTrack: Prod");
        contributions[0].Url.Should().Be("https://x.youtrack.cloud/mcp");
        contributions[0].BearerToken.Should().Be("secret-token");
    }

    [Fact]
    public void BuildContributions_MultipleInstances_YieldsDistinctlyNamedContributions()
    {
        var instances = new List<YouTrackInstance>
        {
            new("Prod", "https://prod.youtrack.cloud/api", "prod-token", string.Empty),
            new("Staging", "https://staging.youtrack.cloud/api", "staging-token", string.Empty),
        };

        var contributions = YouTrackMcpRegistration.BuildContributions(instances);

        contributions.Should().HaveCount(2);
        contributions.Select(contribution => contribution.Name).Should().BeEquivalentTo("YouTrack: Prod", "YouTrack: Staging");
    }
}
