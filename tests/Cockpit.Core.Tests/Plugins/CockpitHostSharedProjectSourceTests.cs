using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Infrastructure.Projects;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="DesktopBackendHost.AddSharedProjectSource"/>/<see cref="DesktopBackendHost.RemoveSharedProjectSource"/> (AC-245):
/// the host's forwarding half of <see cref="ISharedProjectSourceRegistry"/>, exercised through the real DI-resolved
/// registry rather than a mock — <c>Cockpit.Backend.Tests.Projects.SharedProjectSourceRegistryTests</c> already
/// covers the registry's own rules in isolation. Mirrors <see cref="CockpitHostProjectMemorySourceTests"/>.
/// </summary>
public class CockpitHostSharedProjectSourceTests
{
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

        return new DesktopBackendHost(
            "test-plugin",
            "Test Plugin",
            provider,
            Substitute.For<ICockpitActions>(),
            Substitute.For<IPluginStorage>(),
            NullCockpitSessionObserver.Instance,
            new PluginDiagnostics());
    }

    private sealed class _FakeSource(string key) : ISharedProjectSource
    {
        public string Key => key;

        public string SourceName => key;

        public Task<SharedProjectListResult> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectListResult.Success([]));

        public Task<SharedProjectBindingResult> PrepareBindingAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectBindingResult.Failed("not implemented by this fake"));

        public Task<SharedProjectWriteBackResult> WriteBackAsync(string id, SharedProjectDefinitionEdit edit, string baseChecksum, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectWriteBackResult.Failed("not implemented by this fake"));

        public bool CanPublish => false;

        public Task<SharedProjectPublishTargetListResult> ListPublishTargetsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectPublishTargetListResult.Failed("not implemented by this fake"));

        public Task<SharedProjectPublishResult> PublishAsync(string targetId, SharedProjectPublishDefinition definition, CancellationToken cancellationToken) =>
            Task.FromResult(SharedProjectPublishResult.Failed("not implemented by this fake"));
    }
}
