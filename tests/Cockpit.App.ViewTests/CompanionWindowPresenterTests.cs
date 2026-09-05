using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Layout;
using Cockpit.Core.Layout;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Sessions;
using NSubstitute;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-237: the companion window pops up/hides from the tray (via <see cref="CompanionWindowPresenter"/>), stays
/// always-on-top while open, and renders whatever <see cref="ICompanionToolRegistry"/> holds. Dragging and the
/// rounded chrome are a look-at-it check, not asserted here.
/// </summary>
[Collection("avalonia")]
public class CompanionWindowPresenterTests
{
    private static CompanionWindowPresenter _Presenter()
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LayoutSettings()));
        layoutSettingsStore.SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        return new CompanionWindowPresenter(new CompanionToolRegistry(), layoutSettingsStore);
    }

    [Fact]
    public void Show_ThenHide_TogglesTheWindowsVisibility() => HeadlessAvalonia.Run(() =>
    {
        var presenter = _Presenter();

        presenter.Show();
        Assert.True(presenter.IsVisible);

        presenter.Hide();
        Assert.False(presenter.IsVisible);
    });

    [Fact]
    public void Show_CreatesAWindowThatStaysAlwaysOnTopAndOutOfTheTaskbar() => HeadlessAvalonia.Run(() =>
    {
        var presenter = _Presenter();

        presenter.Show();

        Assert.True(presenter.Window!.Topmost);
        Assert.False(presenter.Window!.ShowInTaskbar);
    });

    [Fact]
    public void Show_RendersAToolRegisteredInTheCompanionToolRegistry() => HeadlessAvalonia.Run(() =>
    {
        var registry = new CompanionToolRegistry();
        var testToolView = new Button { Content = "Test tool" };
        registry.Register(
            new CompanionToolRegistration("test.trivial", "Test tool", _ => testToolView),
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            Substitute.For<ICockpitSessionObserver>());

        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LayoutSettings()));
        layoutSettingsStore.SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var presenter = new CompanionWindowPresenter(registry, layoutSettingsStore);

        presenter.Show();

        Assert.Contains(testToolView, presenter.Window!.GetVisualDescendants());
    });

    [Fact]
    public void FirstPartyHost_RegistersWithPersistentStorageAndLiveSessionObserver()
    {
        var registry = new CompanionToolRegistry();
        IReadOnlyDictionary<string, string>? persisted = null;
        var sessions = Substitute.For<ICockpitSessionObserver>();
        var host = new FirstPartyCompanionToolHost(
            registry,
            new PluginStorage(new Dictionary<string, string>(), data => persisted = data),
            sessions);

        Assert.True(host.AddCompanionTool(new CompanionToolRegistration("cockpit.test", "Test", _ => new Border())));

        var context = registry.CreateContext("cockpit.test")!;
        context.Storage.Set("value", "retained");

        var resumedRegistry = new CompanionToolRegistry();
        var resumedHost = new FirstPartyCompanionToolHost(
            resumedRegistry,
            new PluginStorage(persisted!, _ => { }),
            sessions);
        resumedHost.AddCompanionTool(new CompanionToolRegistration("cockpit.test", "Test", _ => new Border()));

        var resumed = resumedRegistry.CreateContext("cockpit.test")!;
        Assert.Same(sessions, resumed.SelectedSession);
        Assert.Equal("retained", resumed.Storage.Get<string>("value"));
    }

    [Fact]
    public void AssistantCompanionTool_UsesTheCoordinatorIndicator() => HeadlessAvalonia.Run(() =>
    {
        var (coordinator, _, _) = AssistantDockHostSwapTests.Build();

        var view = Assert.IsType<AssistantIndicator>(
            coordinator.CreateCompanionTool().CreateView(Substitute.For<ICompanionToolContext>()));

        Assert.Same(coordinator.Indicator, view.DataContext);
    });

    [Fact]
    public void CompanionWindow_ShowsUnavailableAssistantFromFirstPartyHost() => HeadlessAvalonia.Run(() =>
    {
        var registry = new CompanionToolRegistry();
        var (coordinator, _, _) = AssistantDockHostSwapTests.Build();
        coordinator.Indicator.IsFeatureEnabled = false;
        coordinator.Indicator.UnavailableReason = "Assistant is disabled";
        var host = new FirstPartyCompanionToolHost(
            registry,
            new PluginStorage(new Dictionary<string, string>(), _ => { }),
            Substitute.For<ICockpitSessionObserver>());
        host.AddCompanionTool(coordinator.CreateCompanionTool());
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LayoutSettings()));
        layoutSettingsStore.SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var presenter = new CompanionWindowPresenter(registry, layoutSettingsStore);

        presenter.Show();

        var reason = presenter.Window!.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == "Assistant is disabled");
        Assert.True(reason.IsEffectivelyVisible);
    });
}
