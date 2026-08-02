using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// Which shared-project sources the Projects workspace ends up reading from (AC-245). Two plugins offering the
/// same key is the agreed case — the same "first wins, second is refused, nothing throws" rule
/// <see cref="ProjectFieldRegistryTests"/> and <see cref="ProjectOwnershipRegistryTests"/> already cover for their
/// own registries.
/// </summary>
public class SharedProjectSourceRegistryTests
{
    private static ISharedProjectSource Source(string key) => new _FakeSource(key);

    [Fact]
    public void Register_TwoSourcesUnderTheSameKey_KeepsTheFirst()
    {
        var registry = new SharedProjectSourceRegistry();
        var first = Source("depot");
        var second = Source("depot");

        Assert.True(registry.Register(first));
        Assert.False(registry.Register(second));

        Assert.Same(first, Assert.Single(registry.Sources));
    }

    [Fact]
    public void Register_ASourceWithNoKey_IsRefused()
    {
        var registry = new SharedProjectSourceRegistry();

        Assert.False(registry.Register(Source("  ")));

        Assert.Empty(registry.Sources);
    }

    [Fact]
    public void Remove_WithdrawsTheSourceUnderThatKey()
    {
        var registry = new SharedProjectSourceRegistry();
        registry.Register(Source("depot"));

        registry.Remove("depot");

        Assert.Empty(registry.Sources);
    }

    [Fact]
    public void Remove_UnknownKey_IsANoOp()
    {
        var registry = new SharedProjectSourceRegistry();
        registry.Register(Source("depot"));

        registry.Remove("nothing-registered-under-this");

        Assert.Single(registry.Sources);
    }

    [Fact]
    public void AfterRemove_TheSameKeyCanBeRegisteredAgain()
    {
        var registry = new SharedProjectSourceRegistry();
        registry.Register(Source("depot"));
        registry.Remove("depot");

        var replacement = Source("depot");
        Assert.True(registry.Register(replacement));
        Assert.Same(replacement, Assert.Single(registry.Sources));
    }

    [Fact]
    public void TheAppsOwnScan_ResolvesTheRegistry()
    {
        // ProjectsViewModel takes ISharedProjectSourceRegistry as a constructor dependency; a missing marker
        // interface here is the app failing to start, not a quiet degradation — the same reason
        // ProjectFieldRegistryTests carries the identical scan check for its own registry.
        var services = new ServiceCollection();
        services.AddServices(typeof(SharedProjectSourceRegistry).Assembly);

        Assert.IsType<SharedProjectSourceRegistry>(services.BuildServiceProvider().GetService<ISharedProjectSourceRegistry>());
    }

    private sealed class _FakeSource(string key) : ISharedProjectSource
    {
        public string Key => key;

        public string SourceName => key;

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectListResult.Success([]));

        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectBindingResult.Failed("not implemented by this fake"));
    }
}
