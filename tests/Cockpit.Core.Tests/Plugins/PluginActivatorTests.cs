using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

public class PluginActivatorTests : IDisposable
{
    private readonly string _root;

    public PluginActivatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cockpit-plugin-activator-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    // AC-1159: discovery already refuses this manifest, but the activator must not trust a `DiscoveredPlugin`
    // it did not itself validate -- it is what actually loads the assembly in-process with full trust. If the
    // check did not run first, `LoadFromAssemblyPath` would throw on these non-PE bytes instead of returning
    // null, so a passing test here proves the reject happens before any load is attempted.
    [Fact]
    public void Activate_EntryAssemblyEscapesFolder_ReturnsNullWithoutLoading()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "victim")).FullName;
        Directory.CreateDirectory(Path.Combine(_root, "approved-plugin"));
        File.WriteAllText(Path.Combine(_root, "approved-plugin", "entry.dll"), "not-a-real-assembly");
        var manifest = new PluginManifest("victim", "Victim", "1.0.0", "../approved-plugin/entry.dll", 1, null, null, null, null);
        var discovered = new DiscoveredPlugin(folder, "victim", manifest, "sha", PluginLoadDecision.Load);
        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);

        var plugin = activator.Activate(discovered);

        Assert.Null(plugin);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
