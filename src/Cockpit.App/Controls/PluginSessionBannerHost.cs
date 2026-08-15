using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// Renders the plugin-contributed banners (`ICockpitHost.AddSessionBanner`, AC-802) for the session panel it sits
// in: one control per registered item, each built from a `PluginSessionContext` bound to *this* session. Both
// session kinds (SDK chat and TTY) drop this into their layout, under the transcript, above the composer — the
// same "one shared host, wired from both views" shape `PluginSessionHeaderHost` already uses for the header strip
// and `ConsentBannerHost` uses for the consent overlay. Contributes nothing — and takes no space — when no plugin
// registers a banner.
internal sealed class PluginSessionBannerHost : StackPanel
{
    private readonly List<PluginSessionContext> _contexts = [];

    public PluginSessionBannerHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;

        AttachedToVisualTree += (_, _) => _Build();
        DetachedFromVisualTree += (_, _) => _Clear();
        DataContextChanged += (_, _) => _Build();
    }

    private void _Build()
    {
        _Clear();

        var cockpit = Program.Services?.GetService<CockpitViewModel>();
        if (cockpit is null || DataContext is not SessionPanelViewModel session)
        {
            return;
        }

        foreach (var item in cockpit.PluginSessionBanners)
        {
            // Each item gets its own context: disposing them independently keeps one plugin's teardown from
            // silencing another's, and a context is cheap (two event subscriptions).
            var context = new PluginSessionContext(session);
            _contexts.Add(context);
            Children.Add(item.CreateView(context));
        }
    }

    private void _Clear()
    {
        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _contexts.Clear();
        Children.Clear();
    }
}
