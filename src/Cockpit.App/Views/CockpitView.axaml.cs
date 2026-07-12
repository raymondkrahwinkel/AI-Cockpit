using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.SessionSwitching;
using Exclr8.Terminal;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    private INotifyCollectionChanged? _observedSideSections;
    private INotifyCollectionChanged? _observedSideButtons;

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
        _ApplySidebarWidth();

        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.PropertyChanged += OnCockpitPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (e.RootVisual is InputElement root)
        {
            root.RemoveHandler(KeyDownEvent, OnRootKeyDown);
        }

        if (_observedSideSections is not null)
        {
            _observedSideSections.CollectionChanged -= OnPluginContributionsChanged;
            _observedSideSections = null;
        }

        if (_observedSideButtons is not null)
        {
            _observedSideButtons.CollectionChanged -= OnPluginContributionsChanged;
            _observedSideButtons = null;
        }

        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.PropertyChanged -= OnCockpitPropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    // Keeps the column in sync if SidebarWidth changes from elsewhere (e.g. a settings reset) while the
    // view is open — the splitter drag path below updates the VM straight from the settled column width,
    // so this only fires for external changes, not its own drag.
    private void OnCockpitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CockpitViewModel.SidebarWidth))
        {
            _ApplySidebarWidth();
        }
    }

    private void _ApplySidebarWidth()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        _SidebarColumn().Width = new GridLength(cockpit.SidebarWidth);
    }

    // The GridSplitter already clamps the drag itself (the column's MinWidth/MaxWidth), so the settled
    // column width is read back and persisted once dragging stops — not on every DragDelta, which would
    // hammer cockpit.json on every pixel of movement.
    private async void OnSidebarSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        await cockpit.SetSidebarWidthAsync(_SidebarColumn().Width.Value);
    }

    // x:Name on a ColumnDefinition doesn't generate a code-behind field (unlike a Control), so it's
    // reached through the named root Grid instead.
    private ColumnDefinition _SidebarColumn() => RootGrid.ColumnDefinitions[0];

    // Renders the plugin-contributed left-menu buttons and sections (#14) and keeps them in sync: plugins
    // register these during phase-2 init (before this view attaches), and any later addition rebuilds.
    private void _AttachPluginSections()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        _observedSideSections = cockpit.PluginSideSections;
        _observedSideSections.CollectionChanged += OnPluginContributionsChanged;
        _observedSideButtons = cockpit.PluginSideButtons;
        _observedSideButtons.CollectionChanged += OnPluginContributionsChanged;
        _RebuildPluginSections();
    }

    private void OnPluginContributionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _RebuildPluginSections();

    private void _RebuildPluginSections()
    {
        if (PluginSectionsHost is null || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        PluginSectionsHost.Children.Clear();
        if (cockpit.PluginSideButtons.Count == 0 && cockpit.PluginSideSections.Count == 0)
        {
            PluginSectionsHost.IsVisible = false;
            return;
        }

        PluginSectionsHost.IsVisible = true;
        if (this.TryFindResource("CockpitHairlineBrush", out var hairline) && hairline is IBrush brush)
        {
            PluginSectionsHost.Children.Add(new Border { Height = 1, Background = brush, Margin = new Thickness(0, 4) });
        }

        foreach (var launcher in cockpit.PluginSideButtons)
        {
            var button = new Button
            {
                Content = launcher.Title,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            var invoke = launcher.OnInvoke;
            button.Click += (_, _) => invoke();
            PluginSectionsHost.Children.Add(button);
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

        // App-action / plugin shortcuts first (Shift+N = new session, etc.). They never fire while the operator
        // is typing into a text field or the terminal, so a shortcut never hijacks a keystroke.
        if (_TryHandleShortcut(cockpit, e))
        {
            e.Handled = true;
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

    // Matches the pressed key against the configured app-action and plugin shortcuts. Unlike the session
    // switch, this guards the terminal too — a plain Shift+N must type an 'N' into the TUI, not open a session.
    private bool _TryHandleShortcut(CockpitViewModel cockpit, KeyEventArgs e)
    {
        var shortcuts = cockpit.ActiveShortcuts;
        if (shortcuts.Count == 0)
        {
            return false;
        }

        // While typing (text field or terminal) only an "always active" binding — the command palette — fires,
        // so an ordinary shortcut never hijacks a keystroke but the palette stays reachable from anywhere.
        var typing = _IsTypingSurfaceFocused();
        foreach (var binding in shortcuts)
        {
            if (typing && !binding.AlwaysActive)
            {
                continue;
            }

            if (_TryParseGesture(binding.Gesture) is { } gesture && gesture.Matches(e))
            {
                binding.Invoke();
                return true;
            }
        }

        return false;
    }

    // KeyGesture.Parse throws on an invalid/blank gesture string (a half-typed one in Options); treat any
    // unparseable gesture as "no match" rather than letting it crash the key handler.
    private static KeyGesture? _TryParseGesture(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return null;
        }

        try
        {
            return KeyGesture.Parse(gesture);
        }
        catch
        {
            return null;
        }
    }

    // A shortcut must never hijack a keystroke while the operator is typing: true when focus is in a TextBox
    // or anywhere inside the Exclr8 terminal (focus can land on the control or a descendant).
    private bool _IsTypingSurfaceFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is TextBox)
        {
            return true;
        }

        for (var visual = focused as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is TerminalControl)
            {
                return true;
            }
        }

        return false;
    }

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

    // Session context-menu (#right-click): each item's DataContext is the session the menu was opened on;
    // the command lives on the cockpit view model, so route through it with the session as the parameter.
    private void OnRenameSession(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.RenameSessionCommand.Execute(s));

    private void OnDuplicateSession(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.DuplicateSessionCommand.Execute(s));

    private void OnMoveSessionUp(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.MoveSessionUpCommand.Execute(s));

    private void OnMoveSessionDown(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.MoveSessionDownCommand.Execute(s));

    private void OnCloseSessionMenu(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.RequestCloseSessionCommand.Execute(s));

    private void _InvokeSessionCommand(object? sender, Action<CockpitViewModel, SessionPanelViewModel> invoke)
    {
        if (sender is Control { DataContext: SessionPanelViewModel session } && DataContext is CockpitViewModel cockpit)
        {
            invoke(cockpit, session);
        }
    }

    // Inline rename: Enter commits, Escape cancels; losing focus commits an in-progress rename.
    private void OnRenameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionPanelViewModel session })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            session.CommitRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            session.CancelRename();
            e.Handled = true;
        }
    }

    private void OnRenameBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: SessionPanelViewModel session } && session.IsRenaming)
        {
            session.CommitRename();
        }
    }

    // The rename box attaches once when its row is built; focus + select it whenever the row enters rename
    // mode (IsVisible toggling alone does not re-fire attach), and unsubscribe when the row goes away.
    private void OnRenameBoxAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox { DataContext: SessionPanelViewModel session } box)
        {
            return;
        }

        void OnSessionPropertyChanged(object? s, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SessionPanelViewModel.IsRenaming) && session.IsRenaming)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    box.Focus();
                    box.SelectAll();
                });
            }
        }

        session.PropertyChanged += OnSessionPropertyChanged;
        box.DetachedFromVisualTree += (_, _) => session.PropertyChanged -= OnSessionPropertyChanged;

        if (session.IsRenaming)
        {
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }
}
