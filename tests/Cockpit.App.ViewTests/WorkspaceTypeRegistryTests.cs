using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.ViewTests;

/// <summary>
/// <see cref="WorkspaceTypeRegistry"/> — the AC-122 mirror of the widget registry: the first registration of a
/// type id wins, a body is built only for a registered type and scoped to its workspace, and an unregistered id
/// resolves to null so the view shows a placeholder rather than crashing.
/// </summary>
/// <remarks>
/// In the "avalonia" collection because <see cref="CreateBody_ARegisteredType_BuildsAContextScopedToTheWorkspace"/>
/// spins up the headless Avalonia platform: without it, xUnit runs this class in parallel with the other UI tests,
/// two threads race <c>AvaloniaHeadlessPlatform.Initialize</c>, and the test host crashes ("a different thread owns
/// it"). The collection serialises every Avalonia-touching class onto one thread.
/// </remarks>
[Collection("avalonia")]
public class WorkspaceTypeRegistryTests
{
    private static WorkspaceTypeRegistry _NewRegistry() =>
        new(new ServiceCollection().BuildServiceProvider());

    private static WorkspaceTypeRegistration _Type(string id, string title = "T") =>
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

    [Fact]
    public void Register_TheFirstOfATypeId_Wins_AndALaterOneIsRefused()
    {
        var registry = _NewRegistry();

        registry.Register(_Type("autopilot.run", "First"), new FakeStorage(), NullCockpitSessionObserver.Instance).Should().BeTrue();
        registry.Register(_Type("autopilot.run", "Second"), new FakeStorage(), NullCockpitSessionObserver.Instance).Should().BeFalse();
        registry.WorkspaceTypes.Should().ContainSingle().Which.Title.Should().Be("First");
    }

    [Fact]
    public void Register_RaisesChanged_SoALateArrivingTypeIsHeard()
    {
        var registry = _NewRegistry();
        var raised = 0;
        registry.Changed += (_, _) => raised++;

        registry.Register(_Type("a.type"), new FakeStorage(), NullCockpitSessionObserver.Instance);

        raised.Should().Be(1);
    }

    [Fact]
    public void CreateBody_AnUnregisteredType_IsNull_SoTheViewCanShowAPlaceholder()
    {
        _NewRegistry().CreateBody("missing.type", "w1").Should().BeNull();
    }

    [Fact]
    public void CreateBody_ARegisteredType_BuildsAContextScopedToTheWorkspace() => HeadlessAvalonia.Run(() =>
    {
        var registry = _NewRegistry();
        registry.Register(_Type("autopilot.run"), new FakeStorage(), NullCockpitSessionObserver.Instance);

        var built = registry.CreateBody("autopilot.run", "w42");

        built.Should().NotBeNull();
        built!.Value.Context.WorkspaceId.Should().Be("w42");
        built.Value.Registration.CreateBody(built.Value.Context).Should().BeOfType<Panel>();
    });
}
