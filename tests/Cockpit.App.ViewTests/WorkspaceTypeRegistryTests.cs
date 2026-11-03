using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="WorkspaceTypeRegistry"/> — the AC-122 mirror of the widget registry: the first registration of a
/// type id wins.
/// </summary>
[Collection("avalonia")]
public class WorkspaceTypeRegistryTests
{
    private static WorkspaceTypeRegistry _NewRegistry() =>
        new(new ServiceCollection().BuildServiceProvider());

    private static WorkspaceTypeRegistration _Type(string id, string title) =>
        new(id, title, _ => new Panel());

    /// <summary>A do-nothing storage — the registry only carries it through to the context; these tests never read it.</summary>
    private sealed class FakeStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }

        public void SetSecret(string key, string value)
        {
        }

        public string? GetSecret(string key) => null;
    }

    // AC-1750 folded the Changed test in here: the first registration is heard, a refused one changes nothing.
    [Fact]
    public void Register_TheFirstOfATypeId_WinsAndRaisesChanged_AndALaterOneIsRefused()
    {
        var registry = _NewRegistry();
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        Assert.True(registry.Register(_Type("autopilot.run", "First"), new FakeStorage(), NullCockpitSessionObserver.Instance));
        Assert.False(registry.Register(_Type("autopilot.run", "Second"), new FakeStorage(), NullCockpitSessionObserver.Instance));
        Assert.Equal("First", Assert.Single(registry.WorkspaceTypes).Title);
        Assert.Equal(1, raised);
    }
}
