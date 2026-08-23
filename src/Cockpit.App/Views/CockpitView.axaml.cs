using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Core.Shortcuts;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Core.Help;
using Cockpit.Core.Layout;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Core.Projects;
using Cockpit.Core.Workspaces;
using Exclr8.Terminal;

namespace Cockpit.App.Views;

public partial class CockpitView : UserControl
{
    // How often finished sessions are checked against the idle threshold. A sweep is cheap (a comparison per
    // session), and the threshold is in minutes, so half a minute of slack in when a session turns grey is
    // invisible — where a timer per session would not be.
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(30);

    // Often enough that the number means something while you watch an agent work, rarely enough that reading the
    // process table is not itself the thing burning the CPU.
    private static readonly TimeSpan ResourceSampleInterval = TimeSpan.FromSeconds(2);

    // AC-439: how often the cross-workspace claim-collision chip is recomputed. A collision is two agents that
    // already do not see each other reaching for the same resource — nothing about it needs sub-second latency, and
    // a few seconds of lag before the chip appears or clears is invisible against how long a claim is actually held.
    private static readonly TimeSpan ClaimCollisionCheckInterval = TimeSpan.FromSeconds(5);

    // Width of the collapsed sidebar rail — just enough for the expand chevron and a compact New session.
    // Reused for the dock rail's collapsed width too (AC-951): both are the same 40px tab strip.
    private const double CollapsedRailWidth = 40;

    private INotifyCollectionChanged? _observedSideSections;
    private INotifyCollectionChanged? _observedSideButtons;
    private INotifyCollectionChanged? _observedSessions;
    private DispatcherTimer? _idleSweepTimer;
    private DispatcherTimer? _resourceTimer;
    private DispatcherTimer? _claimCollisionTimer;

    public CockpitView()
    {
        InitializeComponent();

        // AC-1040: the workspace strip's own `?`, next to the + that makes one. Hides itself when the page is
        // not there, so a build without the documentation shows the strip exactly as it was.
        if (Program.Services?.GetService<HelpService>() is { } help)
        {
            WorkspacesHelp.Children.Add(new HelpHint(help, new HelpAddress("workspaces", "kinds"), origin: "a “?” on the workspace strip"));
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Top-level shortcuts, tunnelled so a gesture is seen before a focused TTY swallows it into
        // the pty; the per-binding gate still stands down inside a TextBox (Ctrl+Left/Right stays
        // intact). A session switch that fires over the TTY is marked handled so it doesn't reach claude too.
        if (e.RootVisual is InputElement root)
        {
            root.AddHandler(KeyDownEvent, OnRootKeyDown, RoutingStrategies.Tunnel);
        }

        // Clicking anywhere in a pane selects that session (accent border) and focuses its terminal.
        // Tunnelling so the selection lands before a focused terminal or the reorder grip consumes the press.
        SessionGrid?.AddHandler(PointerPressedEvent, OnSessionPanePressed, RoutingStrategies.Tunnel);

        // AC-65: focus landing in a pane selects it too, not just a pointer press. handledEventsToo
        // so a control marking its own focus handled cannot hide the pane change from us.
        SessionGrid?.AddHandler(GotFocusEvent, OnSessionPaneGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);

        _AttachPluginSections();
        _ApplySidebarWidth();
        _ApplyDockRailWidth();
        _RebuildDockPanelContent();

        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.PropertyChanged += OnCockpitPropertyChanged;
            cockpit.SpatialNavigationRequested += OnSpatialNavigationRequested;

            // A closed pane's subtree leaks with a UIA client active: ControlAutomationPeer marks
            // its cache stale but never releases _children, and a passive client never re-queries.
            _observedSessions = cockpit.Sessions;
            _observedSessions.CollectionChanged += OnGridSessionsChanged;

            // The idle sweep lives here rather than in the view model so the view model stays free of timers
            // (and testable by calling the sweep with a time of the test's choosing).
            _idleSweepTimer = new DispatcherTimer { Interval = IdleSweepInterval };
            _idleSweepTimer.Tick += (_, _) => cockpit.SweepIdleSessions(DateTimeOffset.UtcNow);
            _idleSweepTimer.Start();

            // The resource meter (#78) samples on the same principle: the timer lives here, the arithmetic in the
            // view model, so a test can take a sample whenever it likes.
            _resourceTimer = new DispatcherTimer { Interval = ResourceSampleInterval };
            // Fire-and-forget: the WMI read inside runs on the thread pool now, so the tick no longer blocks the UI.
            _resourceTimer.Tick += (_, _) => _ = cockpit.SampleResourcesAsync();
            _resourceTimer.Start();
            _ = cockpit.SampleResourcesAsync();

            // AC-439: collision arithmetic lives on the view model, only the tick lives here. Fire-
            // and-forget rather than awaited: the refresh hops to the thread pool for filesystem
            // canonicalization (RefreshClaimCollisionsAsync), and nothing on the UI thread awaits it.
            _claimCollisionTimer = new DispatcherTimer { Interval = ClaimCollisionCheckInterval };
            _claimCollisionTimer.Tick += (_, _) => _ = cockpit.RefreshClaimCollisionsAsync();
            _claimCollisionTimer.Start();
            _ = cockpit.RefreshClaimCollisionsAsync();

#if DEBUG
            // Leak-sim trigger (dev-only, opt-in): only when COCKPIT_LEAKSIM is set do we poll for a trigger file so
            // the harness can fire a synthetic open+fill+close cycle on demand (CockpitViewModel.RunLeakSimAsync) and
            // read the before/after counts — no real agent. Off by default, so a normal debug run runs no timer.
            if (Cockpit.App.Services.DiagnosticsBackgroundService.LeakDiagnosticsEnabled)
            {
                var leakSimTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                leakSimTimer.Tick += (_, _) =>
                {
                    var trigger = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cockpit-leaksim.trigger");
                    if (System.IO.File.Exists(trigger))
                    {
                        var content = string.Empty;
                        try { content = System.IO.File.ReadAllText(trigger).Trim(); } catch (Exception) { }
                        try { System.IO.File.Delete(trigger); } catch (Exception) { }
                        // "chat" / "chat:<rows>" runs the assistant-chat-window sim; a bare int runs the grid sim with
                        // that many rows; anything else is the default grid sim.
                        if (content.StartsWith("chat", StringComparison.OrdinalIgnoreCase))
                        {
                            var n = 300;
                            var colon = content.IndexOf(':');
                            if (colon >= 0 && int.TryParse(content[(colon + 1)..], out var cr) && cr > 0) n = cr;
                            _ = cockpit.RunAssistantChatLeakSimAsync(n);
                        }
                        else if (int.TryParse(content, out var rows) && rows > 0)
                        {
                            _ = cockpit.RunLeakSimAsync(rows);
                        }
                        else
                        {
                            _ = cockpit.RunLeakSimAsync();
                        }
                    }
                };
                leakSimTimer.Start();
            }
#endif
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _resourceTimer?.Stop();
        _resourceTimer = null;

        _claimCollisionTimer?.Stop();
        _claimCollisionTimer = null;

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
        SessionGrid?.RemoveHandler(GotFocusEvent, OnSessionPaneGotFocus);

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

        if (_observedSessions is not null)
        {
            _observedSessions.CollectionChanged -= OnGridSessionsChanged;
            _observedSessions = null;
        }

        if (DataContext is CockpitViewModel cockpit)
        {
            cockpit.PropertyChanged -= OnCockpitPropertyChanged;
            cockpit.SpatialNavigationRequested -= OnSpatialNavigationRequested;
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
        else if (e.PropertyName is nameof(CockpitViewModel.DockRailWidth) or nameof(CockpitViewModel.OpenDockPanelId))
        {
            _ApplyDockRailWidth();
            if (e.PropertyName == nameof(CockpitViewModel.OpenDockPanelId))
            {
                _RebuildDockPanelContent();
            }
        }
        else if (e.PropertyName == nameof(CockpitViewModel.DockPanels))
        {
            // AC-953: the Assistant registers with its coordinator after this view attaches (and
            // maybe after OpenDockPanelId is restored) and withdraws on undock — so the rail
            // follows the registry, not only the open-panel id.
            _ApplyDockRailWidth();
            _RebuildDockPanelContent();
        }
        else if (e.PropertyName == nameof(CockpitViewModel.SelectedSession))
        {
            // Any selection change — the sidebar-switch shortcut, a sidebar click, or a pane click — moves
            // keyboard focus onto the newly active session's own input so typing lands there straight away.
            _FocusSelectedSessionInput();
        }
    }

    // Ctrl+arrow pane focus: the view answers "which pane is in that direction" from the grid geometry the view
    // model cannot reach, then moves the selection there. A no-op in zoom (no grid on screen — Raymond's call)
    // and when there is no pane that way.
    private void OnSpatialNavigationRequested(object? sender, PaneDirection direction)
    {
        if (DataContext is not CockpitViewModel cockpit
            || cockpit.ShowSinglePane
            || cockpit.SelectedSession is not { } active)
        {
            return;
        }

        var panel = SessionGrid?.GetVisualDescendants().OfType<SessionTilePanel>().FirstOrDefault();
        if (panel?.NeighbourInDirection(active, direction) is SessionPanelViewModel neighbour)
        {
            cockpit.SelectSessionCommand.Execute(neighbour);
        }
    }

    // A closed pane's automation peer only drops from cached children when re-queried, which a
    // passive UIA client never does. Only removals leave a stale entry. Posted at Background
    // priority so the ItemsControl has already pulled the container out before we rebuild.
    private void OnGridSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Move)
        {
            return;
        }

        Dispatcher.UIThread.Post(_RefreshPaneAutomationPeers, DispatcherPriority.Background);
    }

    // Forces the pane grid's automation-peer children to rebuild, dropping the just-removed
    // container's peer (and the closed pane behind it). The tile panel holds the stale reference;
    // the grid is poked too in case a client walked only that far. No-op without active UIA.
    private void _RefreshPaneAutomationPeers()
    {
        if (SessionGrid is null)
        {
            return;
        }

        ControlAutomationPeer.FromElement(SessionGrid)?.GetChildren();
        if (SessionGrid.GetVisualDescendants().OfType<SessionTilePanel>().FirstOrDefault() is { } panel)
        {
            ControlAutomationPeer.FromElement(panel)?.GetChildren();
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

    // AC-951: the dock rail's mirror image of `_ApplySidebarWidth` above — same reasoning, same shape.
    // Collapsed (no panel open): the rail column shrinks to the 40px tab strip and the splitter gives up its
    // grip. Expanded: both are restored to the persisted `DockRailWidth`.
    private void _ApplyDockRailWidth()
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        // AC-953: with nothing registered there is no rail at all — not a 40px strip of empty chrome — so the
        // column gives its width back to the session content rather than merely hiding what stands in it.
        // AC-960: a restored id no registered panel claims (unloaded/removed plugin) collapses the same way.
        var collapsed = cockpit.OpenDockPanelId is not { } openPanelId
            || !cockpit.DockPanels.Any(panel => panel.Id == openPanelId);
        var column = _DockRailColumn();
        column.MinWidth = collapsed ? 0 : LayoutSettings.MinDockRailWidth;
        column.Width = new GridLength(
            !cockpit.HasDockPanels ? 0 : collapsed ? CollapsedRailWidth : cockpit.DockRailWidth);
        RootGrid.ColumnDefinitions[3].Width = new GridLength(collapsed ? 0 : 4);
    }

    private async void OnDockRailSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        await cockpit.SetDockRailWidthAsync(_DockRailColumn().Width.Value);
    }

    private ColumnDefinition _DockRailColumn() => RootGrid.ColumnDefinitions[4];

    // Builds the open panel's content fresh each time it opens — `DockPanelRegistration.CreateView` is a plain
    // factory, not a per-instance context to reattach, so there is nothing to preserve across a close/reopen
    // (unlike the widget panes, which keep their own state in `IWidgetContext.Storage`).
    private void _RebuildDockPanelContent()
    {
        if (DockPanelContent is null || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        DockPanelContent.Content = cockpit.OpenDockPanelId is { } panelId
            ? cockpit.DockPanels.FirstOrDefault(panel => panel.Id == panelId)?.CreateView()
            : null;
    }

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

        var pinned = cockpit.PinnedMenuEntries;
        var collapsed = cockpit.CollapsedMenuEntries;

        PluginSectionsHost.Children.Clear();
        if (pinned.Count == 0 && collapsed.Count == 0)
        {
            PluginSectionsHost.IsVisible = false;
            return;
        }

        PluginSectionsHost.IsVisible = true;

        // Pinned entries are drawn from the one ordered list, so a section the operator moved to the top is at the
        // top — rather than below every plugin that happens to contribute a button.
        foreach (var entry in pinned)
        {
            PluginSectionsHost.Children.Add(_BuildMenuControl(cockpit, entry));
        }

        // AC-937: everything not pinned collapses behind one "Plugins ›" launcher, drawn only when there is
        // something to collapse — an empty flyout would be a door to nothing.
        if (collapsed.Count > 0)
        {
            var collapsedControls = collapsed.Select(entry => _BuildMenuControl(cockpit, entry)).ToList();
            var collapsedBadges = collapsed
                .Select(entry => entry.Button?.Badge)
                .Where(badge => badge is not null)
                .Select(badge => badge!)
                .ToList();
            PluginSectionsHost.Children.Add(new PluginsMenuButton(collapsedControls, collapsedBadges));
        }
    }

    // Shared by the pinned entries drawn directly and the collapsed ones drawn inside the "Plugins ›" flyout (AC-937)
    // — the same PluginLauncherButton/PluginSectionControl instance either way.
    private Control _BuildMenuControl(CockpitViewModel cockpit, PluginMenuEntry entry)
    {
        var pluginId = entry.PluginId;
        Action? onSettings = cockpit.HasPluginSettings(pluginId)
            ? () => _ = cockpit.OpenPluginSettingsAsync(pluginId)
            : null;

        return entry switch
        {
            { Button: { } launcher } => new PluginLauncherButton(launcher.Title, launcher.OnInvoke, onSettings, launcher.Badge),
            { Section: { } section } => new PluginSectionControl(section.Title, section.CreateView(), onSettings),
            _ => throw new InvalidOperationException($"'{pluginId}' contributed a menu entry that is neither a button nor a section."),
        };
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

        // While typing (text field or terminal), most bindings stay gated so they never hijack a keystroke —
        // ShortcutDispatchGate decides which ones survive that. The tunnelling handler marks a match handled, so
        // a shortcut that does fire over the terminal never reaches the PTY.
        var focus = _FocusedInput();
        foreach (var binding in shortcuts)
        {
            if (_TryParseGesture(binding.Gesture) is not { } gesture)
            {
                continue;
            }

            if (!ShortcutDispatchGate.IsBindingLive(binding, gesture, focus))
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

    private ShortcutFocus _FocusedInput() =>
        ShortcutDispatchGate.FocusOf(TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement());

    // Sidebar item click → select that session, and arm a possible drag-reorder (AC-115). Plain event
    // handler (not a command) since the clicked session is the DataContext of the `Border` raising the
    // event, not the item passed as a bindable CommandParameter — simplest wiring for a whole-row click target.
    private void OnSessionItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border { DataContext: SessionPanelViewModel session } || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        cockpit.SelectSessionCommand.Execute(session);

        // Don't arm a drag from the inline rename box — there a press-and-drag is selecting text, not moving the
        // row. Selection above still happened, so a plain click keeps working.
        if (e.Source is Control source && source.FindAncestorOfType<TextBox>(includeSelf: true) is not null)
        {
            return;
        }

        // Only the left button reorders. Arming on any press meant a right-click armed it too, and the row then
        // followed the pointer all the way to whatever the context menu opened — the rename box being the case that
        // showed it (AC-277). The selection above already happened, so a right-click still targets the row it hit.
        if (!e.GetCurrentPoint(sender as Control).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // Arm a possible reorder. Selecting first means a drag that never passes the threshold still did what a
        // click does, rather than the row needing two gestures to both select and move.
        _draggingSession = session;
        _sessionDragOrigin = SessionListStrip?.ItemsPanelRoot is { } panel ? e.GetPosition(panel) : default;
    }

    // Session reordering (AC-115), mirroring the workspace tab strip: two fields and a threshold rather than a full
    // drag-drop session, since the sidebar is one vertical column of rows. The dragged view model instance is
    // stable across a move (unlike a rebuilt tab), so it is held directly and needs no re-resolve by id.
    private SessionPanelViewModel? _draggingSession;
    private Point _sessionDragOrigin;
    private const double SessionDragThreshold = 6;

    private void OnSessionItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_draggingSession is null || SessionListStrip?.ItemsPanelRoot is not { } panel || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        // A move with the button up means the gesture ended somewhere this handler never saw — let go rather than
        // leaving a row glued to the pointer, the same rule the widget drag applies.
        if (!e.GetCurrentPoint(panel).Properties.IsLeftButtonPressed)
        {
            _draggingSession = null;
            return;
        }

        var position = e.GetPosition(panel);
        if (Math.Abs(position.Y - _sessionDragOrigin.Y) < SessionDragThreshold)
        {
            return;
        }

        // Which row container the pointer is over decides the drop position — measured against the rows themselves
        // (their Bounds are in the panel's own coordinates) rather than arithmetic on a fixed row height. The
        // container order matches the visible session order, so this index is a VisibleSessions index.
        var containers = panel.GetVisualChildren().OfType<Control>().ToList();
        var targetIndex = containers.FindIndex(child => position.Y >= child.Bounds.Top && position.Y <= child.Bounds.Bottom);
        if (targetIndex < 0)
        {
            return;
        }

        cockpit.MoveSessionToVisibleIndex(_draggingSession, targetIndex);
        _sessionDragOrigin = position;
    }

    private void OnSessionItemPointerReleased(object? sender, PointerReleasedEventArgs e) => _draggingSession = null;

    // AC-41: awareness banner's "Enable now" — same two-clicks-plus-password path as Options →
    // Security; the password dialog already carries the irreversibility warning, so no confirm
    // needed here. Opening the window is the view's job, but the outcome toast comes from the view model.
    private async void OnEnableEncryption(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit || TopLevel.GetTopLevel(this) is not Window owner)
        {
            return;
        }

        var dialog = new PasswordDialog
        {
            DataContext = new PasswordDialogViewModel(
                "Encrypt your credentials",
                "Your API keys and tokens are encrypted in cockpit.json, and the cockpit asks for this password "
                + "every time it starts.\n\n"
                + "If you forget it, nobody can decrypt them — not you, not us. The only way back is to clear the "
                + "credentials and type them in again; your profiles, sessions, layout and shortcuts survive that. "
                + "You can turn encryption off again at any time, which puts everything back exactly as it was.",
                requiresCurrent: false),
        };

        if (await dialog.ShowDialog<PasswordDialogViewModel?>(owner) is { } password)
        {
            await cockpit.EnableEncryptionFromBannerAsync(password.NewPassword);
        }
    }

    // Clicking anywhere on a workspace tab switches to it, same whole-row target as a session row.
    // The ✕ inside is a Button so its click is handled there and never reaches this — else a
    // bubbling press would select the workspace it's about to close.
    private void OnWorkspaceTabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Border { DataContext: WorkspaceTabViewModel tab } border && DataContext is CockpitViewModel cockpit)
        {
            cockpit.Workspaces.SelectWorkspaceCommand.Execute(tab.Id);

            // Only the left button reorders — the same rule the session rows follow (AC-277). This strip has its own
            // Rename in the tab context menu, so arming on a right-click left the tab following the pointer on the
            // way to the rename box and silently reordered the workspaces. Selecting above already happened.
            if (!e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
            {
                return;
            }

            // Arm a possible reorder. Selecting first means a drag that never passes the threshold still did
            // what a click does, rather than the tab needing two gestures to both switch and move.
            _draggingTab = tab;
            _tabDragOrigin = border.Parent is Control strip ? e.GetPosition(strip) : default;
        }
    }

    // Tab reordering: drag state is two fields, not a full drag-drop session — the strip is one row
    // of small targets, so "which tab, moved far enough" is the whole problem. Threshold keeps a
    // sloppy click (select) from reading as a one-pixel reorder.
    private WorkspaceTabViewModel? _draggingTab;
    private Point _tabDragOrigin;
    private const double TabDragThreshold = 6;

    private void OnWorkspaceTabPointerMoved(object? sender, PointerEventArgs e)
    {
        // Reached by name, not by walking up from the tab: each tab sits inside a generated
        // ContentPresenter, so Border.Parent is that presenter — walking up would always land back
        // on the tab being dragged and nothing would ever move.
        if (_draggingTab is null || WorkspaceTabStrip?.ItemsPanelRoot is not { } strip || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        // A move with the button up means the gesture ended somewhere this handler never saw — let go rather than
        // leaving a tab glued to the pointer, the same rule the widget drag and the session rows apply.
        if (!e.GetCurrentPoint(strip).Properties.IsLeftButtonPressed)
        {
            _draggingTab = null;
            return;
        }

        var position = e.GetPosition(strip);
        if (Math.Abs(position.X - _tabDragOrigin.X) < TabDragThreshold)
        {
            return;
        }

        // Which container the pointer is over decides the drop index — measured against the tabs themselves
        // rather than arithmetic on widths, since they are as wide as their names.
        var containers = strip.GetVisualChildren().OfType<Control>().ToList();
        var targetIndex = containers.FindIndex(child => position.X >= child.Bounds.Left && position.X <= child.Bounds.Right);
        if (targetIndex < 0)
        {
            return;
        }

        var currentIndex = cockpit.Workspaces.Tabs.Select((tab, index) => (tab, index))
            .FirstOrDefault(entry => entry.tab.Id == _draggingTab.Id).index;
        if (targetIndex == currentIndex)
        {
            return;
        }

        // The tab strip is rebuilt on every move, so the dragged tab object is replaced under us — keep the id
        // and re-find it, rather than holding a reference to a tab that no longer exists.
        var draggingId = _draggingTab.Id;
        _ = cockpit.Workspaces.MoveWorkspaceAsync(draggingId, targetIndex);
        _draggingTab = cockpit.Workspaces.Tabs.FirstOrDefault(tab => tab.Id == draggingId);
        _tabDragOrigin = position;
    }

    private void OnWorkspaceTabPointerReleased(object? sender, PointerReleasedEventArgs e) => _draggingTab = null;

    // Double-click a tab to rename it in place — the same inline edit a session row uses.
    private void OnWorkspaceTabDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Border { DataContext: WorkspaceTabViewModel tab })
        {
            tab.BeginRename();
        }
    }

    // The tab's right-click menu. Each item's DataContext is the tab the menu was opened on, so both handlers
    // read it straight off the sender — the same shape as the session rows' context menu.
    private void OnRenameWorkspaceRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceTabViewModel tab })
        {
            tab.BeginRename();
        }
    }

    // Ask-then-close lives on the view model, so the ✕, the context menu and the command palette all take the
    // same path. Two copies of "what is about to be lost" is two chances for the prompt to drift from what
    // closing actually does.
    private void OnCloseWorkspaceRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: WorkspaceTabViewModel tab } && DataContext is CockpitViewModel cockpit)
        {
            _ = cockpit.CloseWorkspaceWithConfirmationAsync(tab.Id);
        }
    }

    // The rename box becomes visible where it was already in the tree, so nothing gives it focus on its own —
    // you had to click it before you could type (Raymond). Selecting everything on the way in makes the first
    // keystroke replace the name, which is the whole point of asking to rename it.
    private void OnWorkspaceRenameAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is TextBox box)
        {
            // Posted, not called: the box is being attached right now, and focus does not stick to a control
            // mid-attach.
            Dispatcher.UIThread.Post(() =>
            {
                box.Focus();
                box.SelectAll();
            });
        }
    }

    // Enter commits, Escape discards — the two keys an inline edit has to honour, and the reason the box is not
    // simply committed on every keystroke.
    private void OnWorkspaceRenameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: WorkspaceTabViewModel tab })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            _CommitWorkspaceRename(tab);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            tab.CancelRename();
            e.Handled = true;
        }
    }

    // Clicking away commits rather than discards: having typed a name, losing it to a stray click is the more
    // annoying of the two outcomes, and Escape is there for the operator who meant to abandon it.
    private void OnWorkspaceRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: WorkspaceTabViewModel tab } && tab.IsRenaming)
        {
            _CommitWorkspaceRename(tab);
        }
    }

    private void _CommitWorkspaceRename(WorkspaceTabViewModel tab)
    {
        if (tab.CommitRename() is { } name && DataContext is CockpitViewModel cockpit)
        {
            cockpit.Workspaces.RenameWorkspaceCommand.Execute((tab.Id, name));
        }
    }

    private void OnExportDashboardPressed(object? sender, RoutedEventArgs e) => _ = _ExportDashboardAsync();

    private void OnImportDashboardPressed(object? sender, RoutedEventArgs e) => _ = _ImportDashboardAsync();

    private async Task _ExportDashboardAsync()
    {
        if (DataContext is not CockpitViewModel cockpit
            || cockpit.Workspaces.Active is not { } dashboard
            || cockpit.Workspaces.ExportActiveDashboard() is not { } json)
        {
            return;
        }

        if (await cockpit.PickDashboardExportPathAsync(dashboard.Name) is { } path)
        {
            await File.WriteAllTextAsync(path, json);
        }
    }

    // Adds a dashboard from a file. A widget this cockpit does not have is skipped and named rather than the
    // whole file being refused (Raymond's call), so the operator is told what to install rather than left with
    // a dashboard that looks broken instead of incomplete.
    private async Task _ImportDashboardAsync()
    {
        if (DataContext is not CockpitViewModel cockpit || await cockpit.PickDashboardToImportAsync() is not { } path)
        {
            return;
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await cockpit.ConfirmAsync("Import dashboard", $"That file could not be read.\n\n{exception.Message}", confirmLabel: "OK");
            return;
        }

        if (await cockpit.Workspaces.ImportDashboardAsync(json) is not { } import)
        {
            await cockpit.ConfirmAsync(
                "Import dashboard",
                "That is not a dashboard this version can read — either it is a different kind of file, or it was exported by a newer build.",
                confirmLabel: "OK");
            return;
        }

        if (!import.IsComplete)
        {
            await cockpit.ConfirmAsync(
                "Imported, with widgets missing",
                $"“{import.Workspace.Name}” was added, but these widgets are not installed here and were left out:\n\n"
                + string.Join("\n", import.MissingWidgetIds.Select(id => $"  • {id}"))
                + "\n\nInstall the plugins that provide them from the store, then import the file again to get them.",
                confirmLabel: "OK");
        }
    }

    // Widget dragging (F0). A pane is moved by rearranging where the grid puts it — never by rebuilding it —
    // so a widget keeps whatever state it holds across a drag, the same rule the session grid learned on
    // 2026-07-13 when a rebuilt pane lost its pty.
    private WidgetPaneViewModel? _draggingWidget;
    private WidgetPaneViewModel? _resizingWidget;

    private void OnWidgetResizePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: WidgetPaneViewModel pane })
        {
            _resizingWidget = pane;
            // The grip is inside the pane; without this the press would also reach the header's drag and the
            // widget would move while being resized.
            e.Handled = true;
        }
    }

    private void OnWidgetHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        // The chrome buttons live in this header; a press on one of them is not the start of a drag.
        if (e.Source is Control source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        if (sender is Control { DataContext: WidgetPaneViewModel pane } header)
        {
            _draggingWidget = pane;

            // Captured so the gesture keeps reporting past the dashboard's edge — without it a
            // widget could never be dragged to another workspace's tab. Handlers still run: they sit
            // on DashboardGrid, an ancestor, and a captured pointer's events bubble as usual.
            e.Pointer.Capture(header);
        }
    }

    // The workspace tab a drag is currently over, if any. Held rather than acted on, for the same reason the
    // grid's ghost is: the move is one write on release, not one per pixel.
    private WorkspaceTabViewModel? _dropTargetTab;

    // Where the gesture currently says the pane will land. Held rather than applied, so the config is written
    // once on release instead of on every pixel of the drag — and so the ghost has something to draw.
    private GridCell? _ghostCell;

    private void OnDashboardPointerMoved(object? sender, PointerEventArgs e)
    {
        var active = _draggingWidget ?? _resizingWidget;
        if (active is null
            || !e.GetCurrentPoint(DashboardGrid).Properties.IsLeftButtonPressed
            || DashboardGrid?.ItemsPanelRoot is not { } grid
            || DataContext is not CockpitViewModel cockpit)
        {
            // A move with the button up means the gesture ended somewhere this handler never saw — let go
            // rather than leaving a pane glued to the pointer.
            _EndWidgetGesture();
            return;
        }

        // Over another workspace's tab, the answer is "not on this grid at all" — so the cell ghost goes away
        // and the tab lights up instead. Only for a move: a resize is about this dashboard's own geometry and
        // means nothing on a tab.
        _dropTargetTab = _draggingWidget is null ? null : _WorkspaceTabAt(e);
        _HighlightDropTargetTab();
        if (_dropTargetTab is not null)
        {
            _ghostCell = null;
            if (WidgetDropGhost is not null)
            {
                WidgetDropGhost.IsVisible = false;
            }

            return;
        }

        var position = e.GetPosition(grid);
        var (columns, rows) = (cockpit.Workspaces.DashboardColumns, cockpit.Workspaces.DashboardRows);
        if (DashboardGridMath.CellAt(position.X, position.Y, grid.Bounds.Width, grid.Bounds.Height, columns, rows) is not { } target)
        {
            return;
        }

        // Answers both gestures the same way: what rectangle would this land on, or null for
        // "nothing legal" (onto a neighbour, off-grid, inverted, over two widgets). Move asks Drop
        // rather than working the cell out here, so the ghost can't promise a landing the release refuses.
        var panes = cockpit.Workspaces.WidgetPanes.Select(pane => (pane.Id, pane.Pane.Cell)).ToList();
        var layout = new DashboardLayout { Columns = columns, Rows = rows };
        _ghostCell = _resizingWidget is not null
            ? DashboardGridMath.Resize(panes, _resizingWidget.Id, target, layout)
            : DashboardGridMath.Drop(panes, active.Id, target, layout) is { } arranged
                ? arranged.First(entry => entry.Id == active.Id).Cell
                : null;

        _ShowGhost(grid, columns, rows);
    }

    private void OnDashboardPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (DataContext is CockpitViewModel cockpit)
        {
            // Applied once, here — the ghost (or the lit tab) showed the answer all along, so the drag itself
            // never touched disk.
            if (_dropTargetTab is { } tab && _draggingWidget is { } moving)
            {
                _ = cockpit.Workspaces.MovePaneToWorkspaceAsync(moving.Id, tab.Id);
            }
            else if (_ghostCell is { } cell)
            {
                if (_resizingWidget is { } resizing)
                {
                    _ = cockpit.Workspaces.ResizePaneAsync(resizing.Id, cell.ColumnEnd - 1, cell.RowEnd - 1);
                }
                else if (_draggingWidget is { } dragging)
                {
                    _ = cockpit.Workspaces.DropPaneAsync(dragging.Id, cell.Column, cell.Row);
                }
            }
        }

        e.Pointer.Capture(null);
        _EndWidgetGesture();
    }

    private void _EndWidgetGesture()
    {
        (_draggingWidget, _resizingWidget, _ghostCell, _dropTargetTab) = (null, null, null, null);
        _HighlightDropTargetTab();
        if (WidgetDropGhost is not null)
        {
            WidgetDropGhost.IsVisible = false;
        }
    }

    // Which workspace tab the pointer is over, or null. Only a dashboard other than the current one
    // counts — WorkspaceTypeRules.Accepts refuses a sessions workspace, and the own tab is a no-op
    // drop. Hit-tested from the strip, not per-tab handlers, since a move rebuilds the tabs.
    private WorkspaceTabViewModel? _WorkspaceTabAt(PointerEventArgs e)
    {
        if (WorkspaceTabStrip?.ItemsPanelRoot is not { } strip || DataContext is not CockpitViewModel cockpit)
        {
            return null;
        }

        var position = e.GetPosition(strip);
        foreach (var container in strip.Children)
        {
            if (container.Bounds.Contains(position)
                && container.DataContext is WorkspaceTabViewModel tab
                && tab.Id != cockpit.Workspaces.Active?.Id
                && cockpit.Workspaces.Settings.Workspaces.Any(workspace => workspace.Id == tab.Id && workspace.Type == WorkspaceType.Dashboard))
            {
                return tab;
            }
        }

        return null;
    }

    // Lights the tab a drop would land on. Set on the container rather than the view model: it lasts exactly
    // as long as the gesture, so it has no business being persisted or reasoned about anywhere else.
    private void _HighlightDropTargetTab()
    {
        if (WorkspaceTabStrip?.ItemsPanelRoot is not { } strip)
        {
            return;
        }

        foreach (var container in strip.Children)
        {
            container.Classes.Set("dropTarget", _dropTargetTab is not null && ReferenceEquals(container.DataContext, _dropTargetTab));
        }
    }

    // Lays the ghost over the cells the gesture would take. Hidden when the answer is "nowhere legal", which is itself the feedback: the pane will not go there.
    private void _ShowGhost(Control grid, int columns, int rows)
    {
        if (WidgetDropGhost is null)
        {
            return;
        }

        if (_ghostCell is not { } cell || columns <= 0 || rows <= 0)
        {
            WidgetDropGhost.IsVisible = false;
            return;
        }

        var (cellWidth, cellHeight) = (grid.Bounds.Width / columns, grid.Bounds.Height / rows);
        WidgetDropGhost.Margin = new Thickness(12 + (cell.Column * cellWidth), 12 + (cell.Row * cellHeight), 0, 0);
        WidgetDropGhost.Width = Math.Max(0, (cell.ColumnSpan * cellWidth) - 8);
        WidgetDropGhost.Height = Math.Max(0, (cell.RowSpan * cellHeight) - 8);
        WidgetDropGhost.IsVisible = true;
    }

    // Widget pane chrome. Each button's DataContext is the pane it sits on, so the handler needs no parameter
    // plumbing — the same shape as the session-row handlers above.
    private void OnWidgetRefreshPressed(object? sender, RoutedEventArgs e) =>
        _WithWidgetPane(sender, pane => pane.Refresh());

    private void OnWidgetRemovePressed(object? sender, RoutedEventArgs e) =>
        _WithWidgetPane(sender, pane =>
        {
            if (DataContext is CockpitViewModel cockpit)
            {
                _ = cockpit.Workspaces.RemovePaneAsync(pane.Id);
            }
        });

    // The ⚙ on a widget pane. The plugin supplies the form's content; the host puts it in the dialog with the
    // Save/Close footer — the same split as a plugin's own settings view, so a widget never builds a window.
    // Saving asks that instance to refresh, which is how its view picks up the config the form just wrote.
    private void OnWidgetConfigPressed(object? sender, RoutedEventArgs e) =>
        _WithWidgetPane(sender, pane =>
        {
            if (DataContext is CockpitViewModel cockpit)
            {
                _ = cockpit.ShowWidgetSettingsAsync(pane);
            }
        });

    private void _WithWidgetPane(object? sender, Action<WidgetPaneViewModel> act)
    {
        if (sender is Control { DataContext: WidgetPaneViewModel pane })
        {
            act(pane);
        }
    }

    // Session context-menu (#right-click): each item's DataContext is the session the menu was opened on;
    // the command lives on the cockpit view model, so route through it with the session as the parameter.
    private void OnRenameSession(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.RenameSessionCommand.Execute(s));

    private void OnDuplicateSession(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.DuplicateSessionCommand.Execute(s));

    private void OnClearSessionContext(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.ClearSessionContextCommand.Execute(s));

    private void OnSetSessionStatus(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.SetSessionStatusCommand.Execute(s));

    private void OnScheduleSessionResume(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.ScheduleSessionResumeCommand.Execute(s));

    private void OnClearSessionStatus(object? sender, RoutedEventArgs e) => _InvokeSessionCommand(sender, (c, s) => c.ClearSessionStatusCommand.Execute(s));

    // AC-674/AC-703: built here, not bound via ItemsSource - a popup ContextMenu can't reach CockpitViewModel via $parent.
    // Populated on Opened, not the item's Click: a second ContextMenu opened from a Click inside an already-open one
    // never showed (closing this menu during click-routing raced the new popup's open) - Opened avoids the race.
    private void OnSessionContextMenuOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { DataContext: SessionPanelViewModel session } menu || DataContext is not CockpitViewModel cockpit)
        {
            return;
        }

        if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => (string?)item.Header == "Move to workspace") is not { } moveItem)
        {
            return;
        }

        var targets = cockpit.Workspaces.Settings.Workspaces
            .Where(workspace => workspace.Type == WorkspaceType.Sessions && workspace.Id != session.WorkspaceId)
            .ToList();

        moveItem.IsEnabled = targets.Count > 0;
        moveItem.ItemsSource = targets
            .Select(workspace =>
            {
                var item = new MenuItem { Header = workspace.Name };
                item.Click += (_, _) => cockpit.MoveSessionToWorkspaceCommand.Execute((session, workspace.Id));
                return item;
            })
            .ToList();
    }

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

    // The project rows' context menu (AC-164). Click handlers rather than command bindings for the same reason the
    // session rows use them: a ContextMenu is not in the ItemsControl's visual tree, so the {$parent[ItemsControl]}
    // binding the row's own ▶ uses cannot reach the cockpit from inside the menu.
    private void OnStartProjectSession(object? sender, RoutedEventArgs e) => _InvokeProjectCommand(sender, (c, p) => c.StartProjectSessionCommand.Execute(p));

    private void OnNewSessionForProject(object? sender, RoutedEventArgs e) => _InvokeProjectCommand(sender, (c, p) => c.NewSessionForProjectCommand.Execute(p));

    private void OnOpenProjectFolder(object? sender, RoutedEventArgs e) => _InvokeProjectCommand(sender, (c, p) => c.OpenProjectFolderCommand.Execute(p));

    private void OnEditProject(object? sender, RoutedEventArgs e) => _InvokeProjectCommand(sender, (c, p) => c.EditProjectCommand.Execute(p));

    private void _InvokeProjectCommand(object? sender, Action<CockpitViewModel, Project> invoke)
    {
        if (sender is Control { DataContext: Project project } && DataContext is CockpitViewModel cockpit)
        {
            invoke(cockpit, project);
        }
    }

    // --- Drag-to-reorder grid panes (#54 follow-up) ---
    // Pane is dimmed and follows the pointer via render transform; nothing moves until release.
    // Reordering goes through the cell list, never the bound collection — that would rebuild the pane.
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

    // Pressing anywhere in a pane makes that session active and (unless on a header button/rename
    // box) focuses its terminal, so click-then-type lands where clicked. Not marked handled.
    private void OnSessionPanePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit
            || _PaneContainerFromSource(e.Source) is not { DataContext: SessionPanelViewModel session } container)
        {
            return;
        }

        cockpit.SelectSessionCommand.Execute(session);

        // SelectableTextBlock joins the list now that this focuses an SDK pane's composer too: the transcript is
        // selectable text, and stealing focus on the press that starts a drag-select would take the selection
        // with it. On a terminal pane there was nothing to exclude — the terminal handles its own selection.
        if (e.Source is not (Button or ToggleButton or TextBox or SelectableTextBlock))
        {
            _FocusInputIn(container);
        }
    }

    // AC-65: focus landing in a pane by any route selects it. Guarded on current selection so
    // _FocusSelectedSessionInput's own focus move is a no-op. AC-704: IsPaneVisible closes a
    // second loop where derealizing a just-left pane forced focus back and fought RefreshPaneVisibility.
    private void OnSessionPaneGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (DataContext is not CockpitViewModel cockpit
            || _PaneContainerFromSource(e.Source) is not { DataContext: SessionPanelViewModel session }
            || ReferenceEquals(cockpit.SelectedSession, session)
            || !session.IsPaneVisible)
        {
            return;
        }

        cockpit.SelectSessionCommand.Execute(session);
    }

    // Puts keyboard focus on the currently selected session's own input, once layout has settled (a newly
    // revealed pane in single/zoom mode isn't realised until then).
    private void _FocusSelectedSessionInput()
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
                    _FocusInputIn(child);
                    return;
                }
            }
        });
    }

    // Walks up from the clicked or focused element to the pane container — the child sitting directly in the
    // tile panel. Internal so a view test can pin the visual-tree walk that both the click and focus paths rely on.
    internal static Control? _PaneContainerFromSource(object? source)
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

    // Puts keyboard focus on whatever this pane types into (terminal or SDK composer). Previously
    // only looked for a terminal, so switching to an SDK session left focus on the session just
    // left. Fixed here, not at the selection-change caller, since the pane click path shares this helper.
    internal static void _FocusInputIn(Control container)
    {
        // AC-636: never across windows — a selection change the operator did not make must not pull the caret out
        // of the assistant's chat pop-out. Guarded here because the pane click path shares this helper, and a click
        // cannot trip it: its window is already active by the time the handler runs.
        if (AutoFocus.WouldTakeTheKeyboardFromAnotherWindow(container))
        {
            return;
        }

        if (container.GetVisualDescendants().OfType<TerminalControl>().FirstOrDefault() is { } terminal)
        {
            terminal.Focus();
            return;
        }

        // By name, not "the first TextBox": a pane also carries the inline rename box and the usage warning's
        // resume prompt, and either would swallow the focus the composer is owed.
        container.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(box => box.Name == "InputBox")?.Focus();
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
