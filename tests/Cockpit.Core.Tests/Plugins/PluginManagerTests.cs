using Cockpit.Infrastructure.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The two-phase orchestration (#14): phase 1 instantiates and configures only the load-decided plugins,
/// phase 2 initializes each with its own host, and one misbehaving plugin never takes the others down.
/// The assembly-loading seam is a delegate, so this exercises the sequencing without a real plugin dll.
/// </summary>
public class PluginManagerTests
{
    [Fact]
    public void LoadAndConfigure_RecordsANeedsConsentPluginAsPendingNotFailed_AndSafeModeInstantiatesNothing()
    {
        var discovered = _Discovered("consent", PluginLoadDecision.NeedsConsent);
        var diagnostics = new PluginDiagnostics();
        var manager = new PluginManager(NullLogger<PluginManager>.Instance, diagnostics);

        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => new FakePlugin("consent"));

        // AC-208: awaiting-approval is recorded so the startup banner and the plugin-store badge can count it …
        var pending = diagnostics.PendingApprovals;
        Assert.Single(pending);
        Assert.Equal("consent", pending[0].FolderId);
        Assert.Equal("consent", pending[0].DisplayName);
        // … but it is not a load failure — the plugin simply has not been reviewed yet.
        Assert.Empty(diagnostics.Failures);
        Assert.Empty(manager.Loaded);

        // Safe mode instantiates no plugins, even one that is load-decided.
        var activated = new List<string>();
        var safe = new PluginManager(NullLogger<PluginManager>.Instance, new PluginDiagnostics(), safeMode: true);

        safe.LoadAndConfigure([_Discovered("keep", PluginLoadDecision.Load)], new ServiceCollection(), candidate =>
        {
            activated.Add(candidate.FolderId);
            return new FakePlugin(candidate.FolderId);
        });

        Assert.Empty(activated);
        Assert.Empty(safe.Loaded);
    }

    private static DiscoveredPlugin _Discovered(string id, PluginLoadDecision decision) => new(
        $"/plugins/{id}", id,
        new PluginManifest(id, id, "1.0", $"{id}.dll", AbstractionsVersion: 1, EntryType: null, MinHostVersion: null, Description: null, Author: null),
        Sha256: "hash", decision);

    private sealed class FakePlugin(string id, bool throwOnConfigure = false) : ICockpitPlugin
    {
        public PluginMetadata Metadata { get; } = new(id, id, "1.0", null, null);
        public int ConfigureCount { get; private set; }
        public int InitializeCount { get; private set; }
        public int DisposeCount { get; private set; }
        public ICockpitHost? ReceivedHost { get; private set; }

        public void ConfigureServices(IServiceCollection services)
        {
            ConfigureCount++;
            if (throwOnConfigure)
            {
                throw new InvalidOperationException("configure failed");
            }
        }

        public void Initialize(ICockpitHost host)
        {
            InitializeCount++;
            ReceivedHost = host;
        }

        public void Dispose() => DisposeCount++;
    }
}
