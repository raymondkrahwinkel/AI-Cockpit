using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

// AC-802: renders `ICockpitHost.AddSessionBanner` contributions for the session panel it sits in — the same
// shared-host shape `PluginSessionHeaderHost` uses. Owns its own IsVisible so it reserves no layout space (margin
// included) whenever it has nothing to show — AC-442's rail-grid test caught the regression when this was a binding.
internal sealed class PluginSessionBannerHost : StackPanel
{
    private readonly List<PluginSessionContext> _contexts = [];
    private readonly List<(Control View, EventHandler<AvaloniaPropertyChangedEventArgs> Handler)> _visibilityWatchers = [];

    public PluginSessionBannerHost()
    {
        Orientation = Orientation.Vertical;
        Spacing = 6;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        IsVisible = false;

        AttachedToVisualTree += (_, _) => _Build();
        DetachedFromVisualTree += (_, _) => _Clear();
        DataContextChanged += (_, _) => _Build();
    }

    private void _Build()
    {
        _Clear();

        var cockpit = Program.Services?.GetService<CockpitViewModel>();
        if (cockpit is null || DataContext is not SessionPanelViewModel { ShowPluginHeaderItems: true } session)
        {
            return;
        }

        foreach (var item in cockpit.PluginSessionBanners)
        {
            // Each item gets its own context: disposing them independently keeps one plugin's teardown from
            // silencing another's, and a context is cheap (two event subscriptions).
            var context = new PluginSessionContext(session);
            _contexts.Add(context);

            var view = item.CreateView(context);
            // A banner decides for itself whether it has anything to say (no open PR, no repo, no gh — see
            // SessionPullRequestBannerControl), often only after an async load; watch for that so this host's own
            // IsVisible follows it instead of freezing at the moment of construction.
            EventHandler<AvaloniaPropertyChangedEventArgs> handler = (_, e) =>
            {
                if (e.Property == IsVisibleProperty)
                {
                    _UpdateVisibility();
                }
            };
            view.PropertyChanged += handler;
            _visibilityWatchers.Add((view, handler));

            Children.Add(view);
        }

        _UpdateVisibility();
    }

    private void _UpdateVisibility() => IsVisible = Children.Any(child => child.IsVisible);

    private void _Clear()
    {
        foreach (var (view, handler) in _visibilityWatchers)
        {
            view.PropertyChanged -= handler;
        }

        _visibilityWatchers.Clear();

        foreach (var context in _contexts)
        {
            context.Dispose();
        }

        _contexts.Clear();
        Children.Clear();
        IsVisible = false;
    }
}
