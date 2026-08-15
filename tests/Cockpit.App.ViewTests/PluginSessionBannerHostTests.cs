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
/// AC-802: <c>ICockpitHost.AddSessionBanner</c> must render in both session kinds through the shared
/// <see cref="PluginSessionBannerHost"/>. Each kind is proven independently here, not assumed from the other.
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

    // Reflects past Program.Services' private setter — the only seam to stub the ambient CockpitViewModel lookup;
    // restored on Dispose so this process-wide static does not leak into another test.
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
