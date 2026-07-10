using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.SessionSwitching;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    private INotifyCollectionChanged? _observedSideSections;

    public CockpitView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Handle the switch gesture on the top-level (window) so it fires regardless of which panel
        // has focus. We tunnel (preview) so the gesture is seen before a focused TTY terminal swallows
        // the keystroke into the pty; a focus guard still bails for a TextBox so its Ctrl+Left/Right
        // word-navigation stays intact. In TTY the switch wins and is marked handled, so it does not
        // also reach claude — pick a different switch modifier (Options) to keep Ctrl+Arrow for the TUI.
        if (e.RootVisual is InputElement root)
        {
            root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        }

        _AttachPluginSections();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.RootVisual is InputElement root)
        {
            root.RemoveHandler(KeyDownEvent, OnRootKeyDown);
        }

        if (_observedSideSections is not null)
        {
            _observedSideSections.CollectionChanged -= OnPluginSideSectionsChanged;
            _observedSideSections = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // Renders the plugin-contributed left-menu sections (#14) and keeps them in sync: plugins register
    // their sections during phase-2 init (before this view attaches), and any later addition rebuilds.
    private void _AttachPluginSections()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        _observedSideSections = cockpit.PluginSideSections;
        _observedSideSections.CollectionChanged += OnPluginSideSectionsChanged;
        _RebuildPluginSections();
    }

    private void OnPluginSideSectionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _RebuildPluginSections();

    private void _RebuildPluginSections()
    {
        if (PluginSectionsHost is null || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        PluginSectionsHost.Children.Clear();
        if (cockpit.PluginSideSections.Count == 0)
        {
            PluginSectionsHost.IsVisible = false;
            return;
        }

        PluginSectionsHost.IsVisible = true;
        if (this.TryFindResource("CockpitHairlineBrush", out var hairline) && hairline is IBrush brush)
        {
            PluginSectionsHost.Children.Add(new Border { Height = 1, Background = brush, Margin = new Thickness(0, 4) });
        }

        foreach (var section in cockpit.PluginSideSections)
        {
            PluginSectionsHost.Children.Add(new PluginSectionControl(section.Title, section.CreateView()));
        }
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var settings = cockpit.CurrentSessionSwitchSettings;
        if (!settings.IsEnabled)
        {
            return;
        }

        if (_TryGetSwitchDirection(e.Key) is not { } direction)
        {
            return;
        }

        if (e.KeyModifiers != _RequiredModifiers(settings.Modifier))
        {
            return;
        }

        // Focus-conflict guard: never steal the gesture while the user is typing in a TextBox, where
        // Ctrl+Left/Right is word-navigation. The TTY terminal is intentionally NOT guarded — the session
        // switch should work there too (the tunnelling above marks it handled so claude never sees it).
        if (_IsFocusInTextBox())
        {
            return;
        }

        if (direction < 0)
        {
            cockpit.SelectPreviousSession();
        }
        else
        {
            cockpit.SelectNextSession();
        }

        e.Handled = true;
    }

    /// <summary>Left/Up = previous (-1), Right/Down = next (+1); any other key = not a switch gesture.</summary>
    private static int? _TryGetSwitchDirection(Key key) => key switch
    {
        Key.Left or Key.Up => -1,
        Key.Right or Key.Down => 1,
        _ => null,
    };

    private static KeyModifiers _RequiredModifiers(SessionSwitchModifier modifier) => modifier switch
    {
        SessionSwitchModifier.CtrlAlt => KeyModifiers.Control | KeyModifiers.Alt,
        SessionSwitchModifier.Alt => KeyModifiers.Alt,
        _ => KeyModifiers.Control,
    };

    // Only a TextBox guards the gesture (Ctrl+Left/Right = word-nav there). The TTY terminal is not
    // guarded: the tunnelling handler catches the switch before the terminal and marks it handled.
    private bool _IsFocusInTextBox() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    /// <summary>Sidebar item click → select that session. Plain event handler (not a command) since the
    /// clicked session is the DataContext of the <see cref="Border"/> raising the event, not the item passed
    /// as a bindable CommandParameter — simplest wiring for a whole-row click target.</summary>
    private void OnSessionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Border { DataContext: SessionPanelViewModel session } && DataContext is CockpitViewModel cockpit)
        {
            cockpit.SelectSessionCommand.Execute(session);
        }
    }
}
