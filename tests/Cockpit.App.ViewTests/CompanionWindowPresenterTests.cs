using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Infrastructure.Plugins;
using Cockpit.App.Services;
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
    [Fact]
    public void Show_ThenHide_TogglesTheWindowsVisibility() => HeadlessAvalonia.Run(() =>
    {
        var layoutSettingsStore = Substitute.For<ILayoutSettingsStore>();
        layoutSettingsStore.LoadAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new LayoutSettings()));
        layoutSettingsStore.SaveAsync(Arg.Any<LayoutSettings>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        var presenter = new CompanionWindowPresenter(new CompanionToolRegistry(), layoutSettingsStore);

        presenter.Show();
        Assert.True(presenter.IsVisible);

        presenter.Hide();
        Assert.False(presenter.IsVisible);

        // AC-1522: the X button must persist the hidden state like the menu toggle does.
        presenter.Show();
        layoutSettingsStore.ClearReceivedCalls();
        var window = presenter.Window ?? throw new InvalidOperationException("Show() did not create the companion window");
        window.CloseButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.False(presenter.IsVisible);
        layoutSettingsStore.Received().SaveAsync(
            Arg.Is<LayoutSettings>(s => !s.CompanionWindowVisible), Arg.Any<CancellationToken>());
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
}
