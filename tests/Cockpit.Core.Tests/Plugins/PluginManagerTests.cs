using Cockpit.App.Plugins;
using Cockpit.Core.Plugins;
using Cockpit.Plugins.Abstractions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The two-phase orchestration (#14): phase 1 instantiates and configures only the load-decided plugins,
/// phase 2 initializes each with its own host, and one misbehaving plugin never takes the others down.
/// The assembly-loading seam is a delegate, so this exercises the sequencing without a real plugin dll.
/// </summary>
public class PluginManagerTests
{
    [Fact]
    public void LoadAndConfigure_InstantiatesAndConfiguresOnlyTheLoadDecidedPlugins()
    {
        var load = _Discovered("keep", PluginLoadDecision.Load);
        var others = new[]
        {
            _Discovered("disabled", PluginLoadDecision.Disabled),
            _Discovered("consent", PluginLoadDecision.NeedsConsent),
            _Discovered("mismatch", PluginLoadDecision.AbstractionsMajorMismatch),
        };
        var plugins = new Dictionary<string, FakePlugin>();
        var activated = new List<string>();
        var manager = _Manager();

        manager.LoadAndConfigure([load, .. others], new ServiceCollection(), candidate =>
        {
            activated.Add(candidate.FolderId);
            return plugins[candidate.FolderId] = new FakePlugin(candidate.FolderId);
        });

        activated.Should().Equal("keep");
        plugins["keep"].ConfigureCount.Should().Be(1);
    }

    [Fact]
    public void Initialize_CallsInitializeOnEachLoadedPluginWithItsOwnHost()
    {
        var discovered = _Discovered("plugin", PluginLoadDecision.Load);
        var plugin = new FakePlugin("plugin");
        var host = Substitute.For<ICockpitHost>();
        var manager = _Manager();
        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => plugin);

        manager.Initialize(_ => host);

        plugin.InitializeCount.Should().Be(1);
        plugin.ReceivedHost.Should().BeSameAs(host);
    }

    [Fact]
    public void LoadAndConfigure_WhenAPluginThrowsWhileConfiguring_DisposesItAndKeepsTheOthers()
    {
        var faulty = _Discovered("faulty", PluginLoadDecision.Load);
        var healthy = _Discovered("healthy", PluginLoadDecision.Load);
        var faultyPlugin = new FakePlugin("faulty", throwOnConfigure: true);
        var healthyPlugin = new FakePlugin("healthy");
        var manager = _Manager();

        manager.LoadAndConfigure([faulty, healthy], new ServiceCollection(),
            candidate => candidate.FolderId == "faulty" ? faultyPlugin : healthyPlugin);
        manager.Initialize(_ => Substitute.For<ICockpitHost>());

        faultyPlugin.DisposeCount.Should().Be(1);
        faultyPlugin.InitializeCount.Should().Be(0);
        healthyPlugin.InitializeCount.Should().Be(1);
    }

    [Fact]
    public void Dispose_DisposesEveryLoadedPlugin()
    {
        var first = new FakePlugin("first");
        var second = new FakePlugin("second");
        var manager = _Manager();
        manager.LoadAndConfigure(
            [_Discovered("first", PluginLoadDecision.Load), _Discovered("second", PluginLoadDecision.Load)],
            new ServiceCollection(),
            candidate => candidate.FolderId == "first" ? first : second);

        manager.Dispose();

        first.DisposeCount.Should().Be(1);
        second.DisposeCount.Should().Be(1);
    }

    [Fact]
    public void LoadAndConfigure_WhenAPluginWasBuiltAgainstANewerSdk_LoadsItButRecordsAWarning()
    {
        var discovered = _Discovered("ahead", PluginLoadDecision.Load);
        var diagnostics = new PluginDiagnostics();
        var manager = new PluginManager(
            NullLogger<PluginManager>.Instance,
            diagnostics,
            hostAbstractionsVersion: new Version(1, 2, 0, 0),
            builtAgainstResolver: _ => new Version(1, 3, 0, 0));

        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => new FakePlugin("ahead"));

        // Loaded despite the drift — an older app running a newer plugin usually works …
        manager.Loaded.Select(plugin => plugin.FolderId).Should().Equal("ahead");
        // … but it is said out loud, as a warning rather than a load failure.
        var issue = diagnostics.ForFolder("ahead");
        issue.Should().NotBeNull();
        issue!.Severity.Should().Be(PluginIssueSeverity.Warning);
        issue.Phase.Should().Be("compatibility");
    }

    [Theory]
    [InlineData(1, 2, 0, 0)] // equal
    [InlineData(1, 1, 0, 0)] // older
    public void LoadAndConfigure_WhenAPluginWasBuiltAgainstAnOlderOrEqualSdk_RecordsNoWarning(int major, int minor, int build, int revision)
    {
        var discovered = _Discovered("fine", PluginLoadDecision.Load);
        var diagnostics = new PluginDiagnostics();
        var manager = new PluginManager(
            NullLogger<PluginManager>.Instance,
            diagnostics,
            hostAbstractionsVersion: new Version(1, 2, 0, 0),
            builtAgainstResolver: _ => new Version(major, minor, build, revision));

        manager.LoadAndConfigure([discovered], new ServiceCollection(), _ => new FakePlugin("fine"));

        manager.Loaded.Select(plugin => plugin.FolderId).Should().Equal("fine");
        diagnostics.Failures.Should().BeEmpty();
    }

    private static PluginManager _Manager() => new(NullLogger<PluginManager>.Instance, new PluginDiagnostics());

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
