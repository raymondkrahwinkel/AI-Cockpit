using System.Reflection;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;

namespace Cockpit.App.ViewTests;

/// <summary>
/// AC-802: the session-banner extension point — a plugin calling <c>ICockpitHost.AddSessionBanner</c> ends up
/// rendered in <em>both</em> session kinds through the one shared <see cref="PluginSessionBannerHost"/>, not
/// wired separately per view. Proven for SDK chat (<see cref="SessionView"/>) and TTY (<see cref="TtyView"/>)
/// independently — AC-802 acceptance criterion 2 explicitly calls for showing both, not one and assuming the
/// other.
/// </summary>
[Collection("avalonia")]
public class PluginSessionBannerHostTests
{
    [Fact]
    public void ARegisteredBanner_RendersInSessionView() => HeadlessAvalonia.Run(() =>
    {
        using var _ = _StubProgramServices(out var marker);

        var view = new SessionView();
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        view.DataContext = new SessionViewModel { ShowPluginHeaderItems = true };
        window.UpdateLayout();

        Assert.Contains(_Host(view).GetVisualDescendants(), control => ReferenceEquals(control, marker));
    });

    [Fact]
    public void ARegisteredBanner_RendersInTtyView() => HeadlessAvalonia.Run(() =>
    {
        using var _ = _StubProgramServices(out var marker);

        var view = new TtyView();
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        view.DataContext = new TtyViewModel { ShowPluginHeaderItems = true };
        window.UpdateLayout();

        Assert.Contains(_Host(view).GetVisualDescendants(), control => ReferenceEquals(control, marker));
    });

    // A plain terminal pane (ShowPluginHeaderItems = false) carries no plugin surface at all — the banner host
    // must not render into it either, the same gate PluginSessionHeaderHost's placement already applies.
    [Fact]
    public void APlainTerminalPane_NeverRendersTheBanner() => HeadlessAvalonia.Run(() =>
    {
        using var _ = _StubProgramServices(out var marker);

        var view = new TtyView();
        var window = new Window { Content = view, Width = 800, Height = 400 };
        window.Show();
        view.DataContext = new TtyViewModel { ShowPluginHeaderItems = false };
        window.UpdateLayout();

        Assert.False(_Host(view).IsVisible);
    });

    private static PluginSessionBannerHost _Host(Control view) =>
        view.GetVisualDescendants().OfType<PluginSessionBannerHost>().Single();

    // Program.Services has a private setter (only Program.Main populates it); reflecting past that is the only
    // seam a test has to make the ambient CockpitViewModel lookup PluginSessionBannerHost relies on — the same
    // lookup PluginSessionHeaderHost already makes for the header strip — resolve to something real. Restored on
    // Dispose so this process-wide static does not leak into another test.
    private static IDisposable _StubProgramServices(out Control marker)
    {
        var cockpit = new CockpitViewModel();
        var registeredMarker = new TextBlock { Text = "plugin-banner" };
        ((IPluginContributionSink)cockpit).AddPluginSessionBannerItem(_ => registeredMarker);
        marker = registeredMarker;

        var services = new ServiceCollection().AddSingleton(cockpit).BuildServiceProvider();
        var property = typeof(Program).GetProperty(nameof(Program.Services), BindingFlags.Public | BindingFlags.Static)!;
        var previous = (IServiceProvider?)property.GetValue(null);
        property.SetValue(null, services);

        return new _Restore(() => property.SetValue(null, previous));
    }

    private sealed class _Restore(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
