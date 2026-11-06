using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
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

    // AC-2025: the pin is checked against the copy that is loaded, and the installed folder stays free once the plugin runs.
    [Fact]
    public async Task Activate_LoadsFromAHashedCopy_RefusesBytesThatDifferFromThePin_AndLeavesTheOriginalOverwritable()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "plugins", "probe")).FullName;
        var original = Path.Combine(folder, "Probe.dll");
        var bytes = await File.ReadAllBytesAsync(typeof(ProbePlugin).Assembly.Location);
        await File.WriteAllBytesAsync(original, bytes);
        var manifest = new PluginManifest("probe", "Probe", "1.0.0", "Probe.dll", 1, typeof(ProbePlugin).FullName, null, null, null);
        var discovered = new DiscoveredPlugin(
            folder, "probe", manifest, await PluginClosureHash.OfInstalledFolderAsync(folder), PluginLoadDecision.Load);
        var activator = new PluginActivator(NullLogger<PluginActivator>.Instance);

        // Changed between discovery and load: the copy hashes differently from the pin, so nothing is loaded.
        await File.WriteAllBytesAsync(original, [.. bytes, 0]);
        Assert.Throws<PluginIntegrityException>(() => activator.Activate(discovered));
        Assert.False(Directory.Exists(Path.Combine(_root, "plugin-shadow", "probe", discovered.Sha256)));

        await File.WriteAllBytesAsync(original, bytes);
        var plugin = activator.Activate(discovered);

        Assert.NotNull(plugin);
        Assert.StartsWith(Path.Combine(_root, "plugin-shadow"), plugin.GetType().Assembly.Location, StringComparison.OrdinalIgnoreCase);
        await File.WriteAllBytesAsync(original, [0]);
    }

    public class ProbePlugin : ICockpitPlugin
    {
        public PluginMetadata Metadata => new("probe", "Probe");

        public void ConfigureServices(IServiceCollection services)
        {
        }

        public void Initialize(ICockpitHost host)
        {
        }

        public void Dispose()
        {
        }
    }

    public void Dispose()
    {
        // The copy the probe loaded from stays locked until the process ends; that is the point of loading from a copy.
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
