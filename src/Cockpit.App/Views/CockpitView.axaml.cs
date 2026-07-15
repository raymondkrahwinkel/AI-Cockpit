using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.App.ViewModels;
using Cockpit.Core.Layout;
using Exclr8.Terminal;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    /// <summary>
    /// How often finished sessions are checked against the idle threshold. A sweep is cheap (a comparison per
    /// session), and the threshold is in minutes, so half a minute of slack in when a session turns grey is
    /// invisible — where a timer per session would not be.
    /// </summary>
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(30);

    // Often enough that the number means something while you watch an agent work, rarely enough that reading the
    // process table is not itself the thing burning the CPU.
    private static readonly TimeSpan ResourceSampleInterval = TimeSpan.FromSeconds(2);

    // Width of the collapsed sidebar rail — just enough for the expand chevron and a compact New session.
    private const double CollapsedRailWidth = 40;

    private INotifyCollectionChanged? _observedSideSections;
    private INotifyCollectionChanged? _observedSideButtons;
    private DispatcherTimer? _idleSweepTimer;
    private DispatcherTimer? _resourceTimer;

    public CockpitView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Handle shortcuts on the top-level (window) so they fire regardless of which panel has focus. We
        // tunnel (preview) so a gesture is seen before a focused TTY terminal swallows the keystroke into the
        // pty; the per-binding gate below still stands down inside a TextBox, so its Ctrl+Left/Right
        // word-navigation stays intact. A session switch that fires over the TTY is marked handled, so it does
        // not also reach claude — rebind it in Options → Shortcuts to keep that gesture for the TUI.
        if (e.RootVisual is InputElement root)
        {
            root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        }

        // Clicking anywhere in a pane selects that session (accent border) and focuses its terminal.
        // Tunnelling so the selection lands before a focused terminal or the reorder grip consumes the press.
        SessionGrid?.AddHandler(PointerPressedEvent, OnSessionPanePressed, RoutingStrategies.Tunnel);

        _AttachPluginSections();
        _ApplySidebarWidth();

        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.PropertyChanged += OnCockpitPropertyChanged;

            // The idle sweep lives here rather than in the view model so the view model stays free of timers
            // (and testable by calling the sweep with a time of the test's choosing).
            _idleSweepTimer = new DispatcherTimer { Interval = IdleSweepInterval };
            _idleSweepTimer.Tick += (_, _) => cockpit.SweepIdleSessions(DateTimeOffset.UtcNow);
            _idleSweepTimer.Start();

            // The resource meter (#78) samples on the same principle: the timer lives here, the arithmetic in the
            // view model, so a test can take a sample whenever it likes.
            _resourceTimer = new DispatcherTimer { Interval = ResourceSampleInterval };
            _resourceTimer.Tick += (_, _) => cockpit.SampleResources();
            _resourceTimer.Start();
            cockpit.SampleResources();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _resourceTimer?.Stop();
        _resourceTimer = null;

        if (_idleSweepTimer is not null)
        {
            _idleSweepTimer.Stop();
            _idleSweepTimer = null;
        }

        if (e.RootVisual is InputElement root)
        {
            root.RemoveHandler(KeyDownEvent, OnRootKeyDown);
        }

        SessionGrid?.RemoveHandler(PointerPressedEvent, OnSessionPanePressed);

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
        if (e.PropertyName is nameof(CockpitViewModel.SidebarWidth) or nameof(CockpitViewModel.SidebarCollapsed))
        {
            _ApplySidebarWidth();
        }
        else if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            // Any selection change — the sidebar-switch shortcut, a sidebar click, or a pane click — moves
            // keyboard focus onto the newly active session's terminal so typing lands there straight away.
            _FocusSelectedSessionTerminal();
        }
    }

    private void _ApplySidebarWidth()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        // Collapsed: the sidebar column shrinks to the slim rail (which holds the expand chevron) and the
        // splitter gives up its grip. The column's own MinWidth (the splitter's drag floor) must be lifted
        // first, or it would refuse to shrink below the sidebar's minimum. Expanded: both are restored.
        var collapsed = cockpit.SidebarCollapsed;
        var column = _SidebarColumn();
        column.MinWidth = collapsed ? 0 : LayoutSettings.MinSidebarWidth;
        column.Width = new GridLength(collapsed ? CollapsedRailWidth : cockpit.SidebarWidth);
        RootGrid.ColumnDefinitions[1].Width = new GridLength(collapsed ? 0 : 4);
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
        // The operator's own order/visibility (#72) changes without the collections changing, so the sidebar
        // listens for that too.
        cockpit.PluginMenuChanged += OnPluginMenuChanged;
        _RebuildPluginSections();
    }

    private void OnPluginContributionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => _RebuildPluginSections();

    private void OnPluginMenuChanged(object? sender, EventArgs e) => _RebuildPluginSections();

    private void _RebuildPluginSections()
    {
        if (PluginSectionsHost is null || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        var entries = cockpit.VisibleMenuEntries;

        PluginSectionsHost.Children.Clear();
        if (entries.Count == 0)
        {
            PluginSectionsHost.IsVisible = false;
            return;
        }

        PluginSectionsHost.IsVisible = true;
        if (this.TryFindResource("CockpitHairlineBrush", out var hairline) && hairline is IBrush brush)
        {
            PluginSectionsHost.Children.Add(new Border { Height = 1, Background = brush, Margin = new Thickness(0, 4) });
        }

        // Buttons and sections are drawn from the one ordered list, so a section the operator moved to the top is at
        // the top — rather than below every plugin that happens to contribute a button.
        foreach (var entry in entries)
        {
            var pluginId = entry.PluginId;
            Action? onSettings = cockpit.HasPluginSettings(pluginId)
                ? () => _ = cockpit.OpenPluginSettingsAsync(pluginId)
                : null;

            Control control = entry switch
            {
                { Button: { } launcher } => new PluginLauncherButton(launcher.Title, launcher.OnInvoke, onSettings),
                { Section: { } section } => new PluginSectionControl(section.Title, section.CreateView(), onSettings),
                _ => throw new InvalidOperationException($"'{pluginId}' contributed a menu entry that is neither a button nor a section."),
            };

            PluginSectionsHost.Children.Add(control);
        }
    }

    private void OnRootKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        // Esc closes the resource panel before anything else looks at the key: it is the open thing on screen, and
        // Esc is what closes the open thing.
        if (e.Key == Key.Escape && cockpit.IsResourcePanelOpen)
        {
            cockpit.CloseResourcePanelCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Every keyboard shortcut — app actions, the session switch, and the plugin-contributed ones — is
        // dispatched from the one configurable table (Options → Shortcuts), so there is a single place that
        // decides what a key press does.
        if (_TryHandleShortcut(cockpit, e))
        {
            e.Handled = true;
        }
    }

    // Matches the pressed key against the configured app-action and plugin shortcuts.
    private bool _TryHandleShortcut(CockpitViewModel cockpit, KeyEventArgs e)
    {
        var shortcuts = cockpit.ActiveShortcuts;
        if (shortcuts.Count == 0)
        {
            return false;
        }

        // While typing (text field or terminal), most bindings stay gated so they never hijack a keystroke.
        // A binding still fires if it is "always active" (the command palette) or its gesture uses two or more
        // modifiers (e.g. Ctrl+Shift+P) — those are commands, not something you type, and are never a lone
        // readline/shell key like Ctrl+R, so intercepting them over the terminal is safe. The session-switch
        // bindings sit in between: live over the terminal (the tunnelling handler marks them handled, so claude
        // never sees them), but not in a text box, where the arrow keys are caret navigation.
        var inTextBox = _IsTextBoxFocused();
        var inTerminal = _IsTerminalFocused();
        foreach (var binding in shortcuts)
        {
            if (_TryParseGesture(binding.Gesture) is not { } gesture)
            {
                continue;
            }

            if (!_IsBindingLive(binding, gesture, inTextBox, inTerminal))
            {
                continue;
            }

            if (gesture.Matches(e))
            {
                binding.Invoke();
                return true;
            }
        }

        return false;
    }

    private static bool _IsBindingLive(ShortcutBinding binding, KeyGesture gesture, bool inTextBox, bool inTerminal)
    {
        if (binding.AlwaysActive || _HasMultipleModifiers(gesture))
        {
            return true;
        }

        if (inTextBox)
        {
            return false;
        }

        return !inTerminal || binding.ActiveInTerminal;
    }

    private static bool _HasMultipleModifiers(KeyGesture gesture) =>
        System.Numerics.BitOperations.PopCount((uint)gesture.KeyModifiers) >= 2;

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
    private bool _IsTypingSurfaceFocused() => _IsTextBoxFocused() || _IsTerminalFocused();

    private bool _IsTextBoxFocused() =>
        TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox;

    private bool _IsTerminalFocused()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
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

    /// <summary>Clicking anywhere on a workspace tab switches to it — same whole-row click target as a session
    /// row, and the same wiring: the tab is the <see cref="Border"/>'s DataContext. The ✕ inside the tab is a
    /// Button, so its click is handled there and never reaches this; a press that bubbles up from it would
    /// otherwise select the workspace it is about to close.</summary>
    private void OnWorkspaceTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Border { DataContext: WorkspaceTabViewModel tab } && DataContext is CockpitViewModel cockpit)
        {
            cockpit.Workspaces.SelectWorkspaceCommand.Execute(tab.Id);
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

    // --- Drag-to-reorder grid panes (#54 follow-up) ----------------------------------------------------
    // The pane is "picked up" (dimmed + follows the pointer via a render transform, which leaves its layout
    // slot untouched) while an accent outline highlights the cell it will drop into. Nothing moves until
    // release — live-swapping mid-drag felt clunky. Reordering goes through the panel's cell list, never the
    // bound collection (moving a session there rebuilds its pane → a pty-less black terminal). The grid is
    // 2-D, so the pane follows the pointer on both axes and drops into whichever cell the pointer is over.
    private SessionPanelViewModel? _draggingPane;
    private SessionTilePanel? _dragPanel;
    private Control? _dragContainer;
    private Point _dragPointerStart;
    private int _dragTarget = -1;

    private void OnPaneDragHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { DataContext: SessionPanelViewModel session } handle
            || !e.GetCurrentPoint(handle).Properties.IsLeftButtonPressed
            || handle.GetVisualAncestors().OfType<SessionTilePanel>().FirstOrDefault() is not { } panel)
        {
            return;
        }

        _draggingPane = session;
        _dragPanel = panel;
        _dragContainer = _PaneContainer(panel, session);
        _dragPointerStart = e.GetPosition(panel);
        _dragTarget = -1;

        if (_dragContainer is not null)
        {
            _dragContainer.ZIndex = 50;
            _dragContainer.Opacity = 0.75;
            _dragContainer.RenderTransform = new TranslateTransform();
        }

        e.Pointer.Capture(handle);
        e.Handled = true;
    }

    private void OnPaneDragHandleMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingPane is null
            || _dragPanel is not { } panel
            || sender is not Control handle
            || !ReferenceEquals(e.Pointer.Captured, handle))
        {
            return;
        }

        var position = e.GetPosition(panel);

        // Lift: follow the pointer on both axes. RenderTransform doesn't affect the pane's layout slot, so
        // the other panes stay put and the panel's cell hit-test reads stable bounds.
        if (_dragContainer?.RenderTransform is TranslateTransform lift)
        {
            lift.X = position.X - _dragPointerStart.X;
            lift.Y = position.Y - _dragPointerStart.Y;
        }

        _dragTarget = panel.CellIndexAt(position);
        _ShowDropIndicator(panel, panel.CellRect(_dragTarget));
        e.Handled = true;
    }

    private void OnPaneDragHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_draggingPane is null)
        {
            return;
        }

        var panel = _dragPanel;
        var dragged = _draggingPane;
        var container = _dragContainer;
        var target = _dragTarget;

        if (container is not null)
        {
            container.ZIndex = 0;
            container.Opacity = 1;
            container.RenderTransform = null;
        }

        if (DropIndicator is not null)
        {
            DropIndicator.IsVisible = false;
        }

        _draggingPane = null;
        _dragPanel = null;
        _dragContainer = null;
        _dragTarget = -1;
        e.Pointer.Capture(null);
        e.Handled = true;

        if (panel is not null && target >= 0)
        {
            panel.PlacePane(dragged, target);
        }
    }

    private static Control? _PaneContainer(SessionTilePanel panel, SessionPanelViewModel session)
    {
        foreach (var child in panel.Children)
        {
            if (ReferenceEquals(child.DataContext, session))
            {
                return child;
            }
        }

        return null;
    }

    // Outlines the target cell (translated from panel space into the overlay's own parent coordinates), or
    // hides the indicator when there's nowhere to drop.
    private void _ShowDropIndicator(SessionTilePanel panel, Rect cell)
    {
        if (DropIndicator is null)
        {
            return;
        }

        if (cell.Width <= 0 || cell.Height <= 0 || DropIndicator.GetVisualParent() is not { } overlayParent)
        {
            DropIndicator.IsVisible = false;
            return;
        }

        if (panel.TranslatePoint(cell.Position, overlayParent) is { } topLeft)
        {
            DropIndicator.Width = cell.Width;
            DropIndicator.Height = cell.Height;
            DropIndicator.RenderTransform = new TranslateTransform(topLeft.X, topLeft.Y);
            DropIndicator.IsVisible = true;
        }
    }

    // Pressing anywhere in a pane makes that session the active one and (unless the press was on an
    // interactive control like a header button or the rename box) puts keyboard focus on its terminal, so a
    // click-then-type lands in the session you just clicked. Not marked handled — the terminal/button still
    // gets the press.
    private void OnSessionPanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit
            || _PaneContainerFromSource(e.Source) is not { DataContext: SessionPanelViewModel session } container)
        {
            return;
        }

        cockpit.SelectSessionCommand.Execute(session);

        if (e.Source is not (Button or ToggleButton or TextBox))
        {
            _FocusTerminalIn(container);
        }
    }

    // Puts keyboard focus on the currently selected session's terminal, once layout has settled (a newly
    // revealed pane in single/zoom mode isn't realised until then).
    private void _FocusSelectedSessionTerminal()
    {
        if (DataContext is not CockpitViewModel cockpit || cockpit.SelectedSession is not { } session)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (SessionGrid?.GetVisualDescendants().OfType<SessionTilePanel>().FirstOrDefault() is not { } panel)
            {
                return;
            }

            foreach (var child in panel.Children)
            {
                if (ReferenceEquals(child.DataContext, session))
                {
                    _FocusTerminalIn(child);
                    return;
                }
            }
        });
    }

    // Walks up from the clicked element to the pane container — the child sitting directly in the tile panel.
    private static Control? _PaneContainerFromSource(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && control.GetVisualParent() is SessionTilePanel)
            {
                return control;
            }
        }

        return null;
    }

    private static void _FocusTerminalIn(Control container)
    {
        foreach (var terminal in container.GetVisualDescendants().OfType<TerminalControl>())
        {
            terminal.Focus();
            return;
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
