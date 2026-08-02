using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="CockpitHost.AddSharedProjectSource"/>/<see cref="CockpitHost.RemoveSharedProjectSource"/> (AC-245):
/// the host's forwarding half of <see cref="ISharedProjectSourceRegistry"/>, exercised through the real DI-resolved
/// registry rather than a mock — <see cref="SharedProjectSourceRegistryTests"/> already covers the registry's own
/// rules in isolation. Mirrors <see cref="CockpitHostProjectMemorySourceTests"/>.
/// </summary>
public class CockpitHostSharedProjectSourceTests
{
    [Fact]
    public void AddThenRemove_TakesTheSourceOutOfSharedProjectSources()
    {
        var host = _BuildHost();
        host.AddSharedProjectSource(new _FakeSource("depot"));

        host.RemoveSharedProjectSource("depot");

        Assert.Empty(host.SharedProjectSources);
    }

    [Fact]
    public void Remove_AKeyNeverRegistered_LeavesOtherSourcesUntouched()
    {
        var host = _BuildHost();
        host.AddSharedProjectSource(new _FakeSource("depot"));

        host.RemoveSharedProjectSource("notes");

        Assert.Single(host.SharedProjectSources);
    }

    [Fact]
    public void RemoveThenAdd_TheSameKey_RegistersTheNewSource()
    {
        // The live-refresh case DepotSettingsControl.Save leans on: a key just freed must be immediately
        // re-registrable, not stuck refused as "already taken" by what was just removed.
        var host = _BuildHost();
        var original = new _FakeSource("depot");
        host.AddSharedProjectSource(original);
        host.RemoveSharedProjectSource("depot");

        var replacement = new _FakeSource("depot");
        host.AddSharedProjectSource(replacement);

        Assert.Same(replacement, Assert.Single(host.SharedProjectSources));
    }

    [Fact]
    public void Add_ASecondPluginUnderTheSameKey_IsIgnored()
    {
        var host = _BuildHost();
        var first = new _FakeSource("depot");
        host.AddSharedProjectSource(first);

        host.AddSharedProjectSource(new _FakeSource("depot"));

        Assert.Same(first, Assert.Single(host.SharedProjectSources));
    }

    private static ICockpitHost _BuildHost()
    {
        var services = new ServiceCollection();
        services.AddServices(typeof(SharedProjectSourceRegistry).Assembly);
        var provider = services.BuildServiceProvider();

        return new CockpitHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<IPluginContributionSink>(),
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            Substitute.For<IPluginDialogHost>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }

    private sealed class _FakeSource(string key) : ISharedProjectSource
    {
        public string Key => key;

        public string SourceName => key;

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectListResult.Success([]));
    }
}
