using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.Plugin.GitStatus.Tests;

// What `GitStatusPlugin.Initialize` registers (AC-522): one settings view, one session-header item,
// the workflow steps — and, the point of this test, no side-menu button and no side-menu section. Before
// AC-522 this same count would have found one side-menu button ("Git status"); its absence here is the
// regression this guards against.
//
// Mirrors the counting pattern `YouTrackPluginLoadTests` uses in the host's own test suite, kept local
// here instead: a host test that references a plugins-dev project pulls it into the main solution, which this
// repo deliberately keeps separate — this project already references the plugin directly, so no
// `PluginActivator`/reflection loading is needed to prove the same thing.
public class GitStatusPluginLoadTests
{
    [Fact]
    public void Initialize_RegistersTheHeaderItemAndWorkflowSteps_ButNoSideMenuButtonOrSection()
    {
        var plugin = new GitStatusPlugin();
        plugin.ConfigureServices(new ServiceCollection());

        var host = new FakeCockpitHost(new FakeCockpitActions());
        plugin.Initialize(host);

        Assert.Equal(1, host.SettingsRegistered);
        Assert.Equal(1, host.SessionHeaderItemsRegistered);
        Assert.Equal(GitWorkflowSteps.All().Count(), host.WorkflowSteps.Count);
        Assert.Empty(host.SideMenuButtons);
        Assert.Empty(host.SideMenuSections);

        plugin.Dispose();
    }
}
