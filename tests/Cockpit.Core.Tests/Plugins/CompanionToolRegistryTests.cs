using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The companion-tool contribution point's registry: a plugin registers a mini-tool
/// (<c>ICockpitHost.AddCompanionTool</c>) and the cockpit's pop-out companion window reads them back — the same
/// shape as <see cref="IWidgetRegistry"/>.
/// </summary>
public class CompanionToolRegistryTests
{
    private static IPluginStorage _InMemoryStorage() => new PluginStorage(new Dictionary<string, string>(), _ => { });

    [Fact]
    public void ARegisteredTool_BecomesAvailableInTheRegistry()
    {
        var registry = new CompanionToolRegistry();

        var registered = registry.Register(
            new CompanionToolRegistration("assistant.indicator", "Assistant", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        Assert.True(registered);
        Assert.Equal("assistant.indicator", Assert.Single(registry.Tools).Id);
    }

    /// <summary>Two plugins can claim one tool id — the first one wins, the second is refused rather than listed beside it.</summary>
    [Fact]
    public void ASecondPluginClaimingTheSameToolId_IsRefusedRatherThanListedTwice()
    {
        var registry = new CompanionToolRegistry();
        registry.Register(
            new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        var registeredAgain = registry.Register(
            new CompanionToolRegistration("tools.clock", "Clock (the other one)", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        Assert.False(registeredAgain);
        Assert.Equal("Clock", Assert.Single(registry.Tools).Title);
    }

    [Fact]
    public void Register_RaisesChanged()
    {
        var registry = new CompanionToolRegistry();
        var raised = false;
        registry.Changed += (_, _) => raised = true;

        registry.Register(
            new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()),
            _InMemoryStorage(),
            Substitute.For<ICockpitSessionObserver>());

        Assert.True(raised);
    }

    /// <summary>
    /// AC-237 code review: the context's Storage and SelectedSession must be the registering plugin's own, not an
    /// ephemeral stand-in — a value a tool writes has to still be there in a context built later over the same
    /// plugin storage, the way it would be after a restart reloads that storage from disk.
    /// </summary>
    [Fact]
    public void CreateContext_UsesTheRegisteringPluginsOwnStorageAndSession()
    {
        var registry = new CompanionToolRegistry();
        var pluginStorage = _InMemoryStorage();
        var sessions = Substitute.For<ICockpitSessionObserver>();
        registry.Register(new CompanionToolRegistration("tools.clock", "Clock", _ => new Border()), pluginStorage, sessions);

        registry.CreateContext("tools.clock")!.Storage.Set("format", "24h");
        var reopened = registry.CreateContext("tools.clock")!;

        Assert.Equal("24h", reopened.Storage.Get<string>("format"));
        Assert.Same(sessions, reopened.SelectedSession);
    }
}
