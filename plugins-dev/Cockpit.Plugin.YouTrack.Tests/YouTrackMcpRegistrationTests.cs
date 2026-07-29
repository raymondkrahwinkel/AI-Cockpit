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
        Assert.Equal(expected, YouTrackMcpRegistration.DeriveMcpEndpoint(instanceBaseUrl));
    }

    [Fact]
    public void BuildContributions_NoInstances_ReturnsNothing()
    {
        Assert.Empty(YouTrackMcpRegistration.BuildContributions([]));
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

        Assert.Empty(YouTrackMcpRegistration.BuildContributions(instances));
    }

    [Fact]
    public void BuildContributions_FullyConfiguredInstance_YieldsOneNamedHttpContribution()
    {
        var instances = new List<YouTrackInstance>
        {
            new("Prod", "https://x.youtrack.cloud/api", "secret-token", "PROJ"),
        };

        var contributions = YouTrackMcpRegistration.BuildContributions(instances);

        Assert.Single(contributions);
        Assert.Equal("YouTrack: Prod", contributions[0].Name);
        Assert.Equal("https://x.youtrack.cloud/mcp", contributions[0].Url);
        Assert.Equal("secret-token", contributions[0].BearerToken);
    }

    [Fact]
    public void BuildContributions_InstanceWithMcpTurnedOff_IsSkipped()
    {
        // Fully configured, but the operator unticked "add this instance's MCP server to sessions" (AC-11).
        var instances = new List<YouTrackInstance>
        {
            new("Prod", "https://x.youtrack.cloud/api", "secret-token", "PROJ", AddMcpToSessions: false),
        };

        Assert.Empty(YouTrackMcpRegistration.BuildContributions(instances));
    }

    [Fact]
    public void ManagedServerNames_CoversEveryInstance_EvenIncompleteOrOptedOut()
    {
        // The migration reclaims what an earlier version pushed, so it must name every instance — including one
        // now incomplete or with MCP turned off, whose entry may still be sitting in the registry.
        var instances = new List<YouTrackInstance>
        {
            new("Prod", "https://x.youtrack.cloud/api", "token", string.Empty),
            new("No token", "https://x.youtrack.cloud/api", string.Empty, string.Empty),
            new("Opted out", "https://x.youtrack.cloud/api", "token", string.Empty, AddMcpToSessions: false),
        };

        Assert.Equivalent(
            new object[] { "YouTrack: Prod", "YouTrack: No token", "YouTrack: Opted out" },
            YouTrackMcpRegistration.ManagedServerNames(instances));
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

        Assert.Equal(2, System.Linq.Enumerable.Count(contributions));
        Assert.Equivalent(
            new object[] { "YouTrack: Prod", "YouTrack: Staging" },
            contributions.Select(contribution => contribution.Name));
    }
}
