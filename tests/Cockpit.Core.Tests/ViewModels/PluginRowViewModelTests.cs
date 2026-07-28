using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Plugins;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// <see cref="PluginRowViewModel"/> reading recorded <see cref="PluginFailure"/> entries (#184): "loaded", "its
/// last load/compatibility issue" and "did its MCP contribution fail afterwards" are three independent facts —
/// a plugin that never activated reads differently from one that loaded fine but had a contribution fail
/// afterwards, and the two must stay visible independently rather than one hiding the other.
/// </summary>
public class PluginRowViewModelTests
{
    [Fact]
    public void NoFailureRecorded_ReportsNoFailureAndNoMcpIssue()
    {
        var row = new PluginRowViewModel(_Discovered(PluginLoadDecision.Load));

        Assert.False(row.HasFailure);
        Assert.False(row.HasMcpContributionFailure);
        Assert.Equal(string.Empty, row.FailureText);
        Assert.Equal("Enabled — active this session", row.StatusText);
    }

    [Fact]
    public void FailedDuringInitialize_ReportsAFailedToLoadMessage()
    {
        var failures = new[] { new PluginFailure("plugin", "Plugin", "initialize", "boom") };
        var row = new PluginRowViewModel(_Discovered(PluginLoadDecision.Load), failures: failures);

        Assert.True(row.HasFailure);
        Assert.Equal("Failed to load: boom", row.FailureText);
        Assert.Equal("Failed to load — see below", row.StatusText);
    }

    [Fact]
    public void McpServerContributionFailedAfterLoading_StaysEnabledButFlagsTheContributionSeparately()
    {
        var failures = new[] { new PluginFailure("plugin", "Plugin", "mcp-server", "disk is full") };
        var row = new PluginRowViewModel(_Discovered(PluginLoadDecision.Load), failures: failures);

        Assert.False(row.HasFailure);
        Assert.True(row.HasMcpContributionFailure);
        Assert.Equal("Its MCP server contribution failed: disk is full", row.McpContributionFailureText);
        Assert.Equal("Enabled — active this session", row.StatusText);
    }

    [Fact]
    public void CompatibilityWarning_StaysEnabledAndShowsTheRecordedSentenceAsIs()
    {
        var failures = new[] { new PluginFailure("plugin", "Plugin", "compatibility", "Built against a newer SDK.", PluginIssueSeverity.Warning) };
        var row = new PluginRowViewModel(_Discovered(PluginLoadDecision.Load), failures: failures);

        Assert.True(row.HasFailure);
        Assert.Equal("Built against a newer SDK.", row.FailureText);
        Assert.Equal("Enabled — active this session", row.StatusText);
    }

    [Fact]
    public void InitializeFailureFollowedByALaterMcpContributionFailure_BothFactsSurviveIndependently()
    {
        // The order a real run would produce (#184): PluginManager records "initialize" synchronously; the
        // plugin's fire-and-forget AddMcpServer can still fail afterwards, on its own background continuation.
        var failures = new[]
        {
            new PluginFailure("plugin", "Plugin", "initialize", "threw during Initialize"),
            new PluginFailure("plugin", "Plugin", "mcp-server", "disk is full"),
        };
        var row = new PluginRowViewModel(_Discovered(PluginLoadDecision.Load), failures: failures);

        // The plugin never became operative — that must not be hidden behind the contribution failure that
        // happens to be the more recent entry.
        Assert.Equal("Failed to load — see below", row.StatusText);
        Assert.Equal("Failed to load: threw during Initialize", row.FailureText);
        // ... yet the MCP fact is still readable on its own, not lost because one slot could only hold one failure.
        Assert.True(row.HasMcpContributionFailure);
        Assert.Equal("Its MCP server contribution failed: disk is full", row.McpContributionFailureText);
    }

    private static DiscoveredPlugin _Discovered(PluginLoadDecision decision) => new(
        "/plugins/plugin", "plugin",
        new PluginManifest("plugin", "Plugin", "1.0", "plugin.dll", AbstractionsVersion: 1, EntryType: null, MinHostVersion: null, Description: null, Author: null),
        Sha256: "hash", decision);
}
