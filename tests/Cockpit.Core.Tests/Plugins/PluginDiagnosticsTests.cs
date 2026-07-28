using Cockpit.App.Plugins;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="PluginDiagnostics.ForFolder"/> (#184): a folder can accumulate more than one recorded failure —
/// a compatibility warning at load time, then a runtime failure from a contribution that throws afterwards
/// (e.g. <see cref="CockpitHost.AddMcpServer"/>). The manager row needs the plugin's current state, which is
/// the last one recorded, not the first.
/// </summary>
public class PluginDiagnosticsTests
{
    [Fact]
    public void ForFolder_NoFailureRecorded_ReturnsNull()
    {
        var diagnostics = new PluginDiagnostics();

        Assert.Null(diagnostics.ForFolder("unknown"));
    }

    [Fact]
    public void ForFolder_OneFailureRecorded_ReturnsIt()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.Record("plugin", "Plugin", "load", "boom");

        var failure = diagnostics.ForFolder("plugin");

        Assert.NotNull(failure);
        Assert.Equal("load", failure!.Phase);
        Assert.Equal("boom", failure.Error);
    }

    [Fact]
    public void ForFolder_MultipleNonActivationFailuresRecordedForTheSameFolder_ReturnsTheMostRecentOne()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.Record("plugin", "Plugin", "compatibility", "built against a newer SDK", PluginIssueSeverity.Warning);
        diagnostics.Record("plugin", "Plugin", "mcp-server", "disk is full");

        var failure = diagnostics.ForFolder("plugin");

        Assert.NotNull(failure);
        Assert.Equal("mcp-server", failure!.Phase);
        Assert.Equal("disk is full", failure.Error);
    }

    [Fact]
    public void ForFolder_AnActivationFailure_AlwaysWinsOverALaterContributionFailure()
    {
        // A plugin's Initialize can fire-and-forget AddMcpServer and then itself throw further down — the
        // "initialize" failure is recorded synchronously, but the "mcp-server" one can land afterwards, once the
        // background continuation completes (#184). The plugin never became operative either way; that fact must
        // not be hidden behind the contribution failure that happens to be more recent.
        var diagnostics = new PluginDiagnostics();
        diagnostics.Record("plugin", "Plugin", "initialize", "threw during Initialize");
        diagnostics.Record("plugin", "Plugin", "mcp-server", "disk is full");

        var failure = diagnostics.ForFolder("plugin");

        Assert.NotNull(failure);
        Assert.Equal("initialize", failure!.Phase);
        Assert.Equal("threw during Initialize", failure.Error);
    }

    [Fact]
    public void ForFolder_DoesNotMixUpFailuresFromDifferentFolders()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.Record("first", "First", "load", "first failure");
        diagnostics.Record("second", "Second", "mcp-server", "second failure");

        Assert.Equal("first failure", diagnostics.ForFolder("first")!.Error);
        Assert.Equal("second failure", diagnostics.ForFolder("second")!.Error);
    }

    [Fact]
    public void AllForFolder_ReturnsEveryRecordedFailureForThatFolder_OldestFirst()
    {
        var diagnostics = new PluginDiagnostics();
        diagnostics.Record("plugin", "Plugin", "compatibility", "built against a newer SDK", PluginIssueSeverity.Warning);
        diagnostics.Record("plugin", "Plugin", "mcp-server", "disk is full");
        diagnostics.Record("other", "Other", "load", "unrelated");

        var all = diagnostics.AllForFolder("plugin");

        Assert.Equal(2, all.Count);
        Assert.Equal("compatibility", all[0].Phase);
        Assert.Equal("mcp-server", all[1].Phase);
    }
}
