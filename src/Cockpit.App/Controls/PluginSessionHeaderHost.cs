using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;

namespace Cockpit.App.Controls;

/// <summary>
/// Renders the plugin-contributed header items (<c>ICockpitHost.AddSessionHeaderItem</c>) for the session panel
/// it sits in: one control per registered item, each built from a <see cref="PluginSessionContext"/> bound to
/// <em>this</em> session. Both session kinds (SDK chat and TTY terminal) drop this into their header, so the
/// wiring lives here once instead of twice in two code-behinds. Contributes nothing — and takes no space — when
/// no plugin registers a header item.
/// </summary>
internal sealed class PluginSessionHeaderHost : StackPanel
{
    private readonly List<PluginSessionContext> _contexts = [];

    public PluginSessionHeaderHost()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        VerticalAlignment = VerticalAlignment.Center;

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

        // The menu comes first: it is always there, so it is where the hand goes, and it should not move sideways every
        // time a badge appears or a session stops tracking a ticket. The badges — which come and go — sit after it.
        if (cockpit.PluginSessionHeaderActions.Count > 0)
        {
            var menuContext = new PluginSessionContext(session);
            _contexts.Add(menuContext);

            Children.Add(_ActionsMenu(cockpit, menuContext));
        }

        foreach (var item in cockpit.PluginSessionHeaderItems)
        {
            // Each item gets its own context: disposing them independently keeps one plugin's teardown from
            // silencing another's, and a context is cheap (two event subscriptions).
            var context = new PluginSessionContext(session);
            _contexts.Add(context);
            Children.Add(item.CreateView(context));
        }
    }

    // Detaching without this leaves every context subscribed to a session panel that is on its way out.
    private static Control _ActionsMenu(CockpitViewModel cockpit, PluginSessionContext context)
    {
        var button = new Button
        {
            Content = CockpitIcons.Icon(MaterialIconKind.DotsHorizontal),
            Padding = new Thickness(7, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };

        ToolTip.SetTip(button, "What the plugins can do with this session");

        button.Click += (_, _) =>
        {
            var items = cockpit.PluginSessionHeaderActions
                .Select(action =>
                {
                    var item = new MenuItem
                    {
                        Header = action.Icon is { Length: > 0 } icon ? $"{icon}  {action.Title}" : action.Title,
                    };

                    item.Click += (_, _) => action.Invoke(context);

                    return item;
                })
                .ToList();

            var menu = new ContextMenu { ItemsSource = items, PlacementTarget = button };
            menu.Open(button);
        };

        return button;
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
