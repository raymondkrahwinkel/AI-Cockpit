using Cockpit.Plugins.Abstractions;
using NSubstitute;

namespace Cockpit.Plugin.YouTrack.Tests;

// AC-212/AC-217: the YouTrack provider advertises the READ-tool MCP server(s) a consumer (Autopilot) scopes into a
// planning session — the JetBrains YouTrack MCP server for each fully-configured, opted-in instance, named exactly as
// `YouTrackMcpRegistration` mounts it. A source-triggered planning round adds these so the CEO can read the
// issue and pull an epic's children, while the tracker's write tools stay out of planning.
public class YouTrackTrackerReadToolsTests
{
    [Fact]
    public void ReadToolMcpServerNames_ForAConfiguredOptedInInstance_NamesItsJetBrainsMcpServer()
    {
        var provider = _ProviderWith(new YouTrackInstance("Personal", "https://x.youtrack.cloud/api", "token", "AC"));

        Assert.Equal("YouTrack: Personal", Assert.Single(provider.ReadToolMcpServerNames));
    }

    [Fact]
    public void ReadToolMcpServerNames_SkipsAnIncompleteOrOptedOutInstance()
    {
        var provider = _ProviderWith(
            new YouTrackInstance("No token", "https://x.youtrack.cloud/api", string.Empty, string.Empty),
            new YouTrackInstance("Opted out", "https://y.youtrack.cloud/api", "token", string.Empty, AddMcpToSessions: false));

        Assert.Empty(provider.ReadToolMcpServerNames);
    }

    [Fact]
    public void ReadToolMcpServerNames_NoInstances_IsEmpty()
    {
        var provider = new YouTrackTrackerProvider(new YouTrackSettings(Substitute.For<IPluginStorage>()));

        Assert.Empty(provider.ReadToolMcpServerNames);
    }

    private static YouTrackTrackerProvider _ProviderWith(params YouTrackInstance[] instances)
    {
        var storage = Substitute.For<IPluginStorage>();
        storage.Get<List<YouTrackInstance>>("instances").Returns([.. instances]);
        return new YouTrackTrackerProvider(new YouTrackSettings(storage));
    }
}
