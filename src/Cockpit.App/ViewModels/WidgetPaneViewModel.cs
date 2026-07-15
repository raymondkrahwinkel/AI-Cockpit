using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.ViewModels;

/// <summary>
/// One placed widget as the dashboard renders it: the pane's chrome (title, ↻, ⚙, ✕) plus the control the
/// plugin built for this instance. The view is created once, here, and kept — rebuilding it on every layout
/// pass would throw away whatever state the widget holds, the same mistake the session grid made on
/// 2026-07-13 when a dragged pane was rebuilt and lost its pty.
/// </summary>
public sealed class WidgetPaneViewModel
{
    private readonly WidgetContext _context;

    public WidgetPaneViewModel(WorkspacePane pane, WidgetRegistration registration, WidgetContext context)
    {
        Pane = pane;
        _context = context;
        Title = registration.Title;
        Icon = registration.Icon;
        HasConfig = registration.HasConfig;
        View = registration.CreateView(context);
        _configView = registration.CreateConfigView;
    }

    private readonly Func<IWidgetContext, Control>? _configView;

    public WorkspacePane Pane { get; }

    public string Id => Pane.Id;

    public string Title { get; }

    public string Icon { get; }

    /// <summary>Gates the pane header's ⚙: false means this widget declared no settings form, so no gear is shown at all.</summary>
    public bool HasConfig { get; }

    /// <summary>The plugin's control for this instance, built once.</summary>
    public Control View { get; }

    public int Column => Pane.Cell.Column;

    public int Row => Pane.Cell.Row;

    public int ColumnSpan => Pane.Cell.ColumnSpan;

    public int RowSpan => Pane.Cell.RowSpan;

    /// <summary>The ↻ on the pane header, and what a saved settings form raises — the widget re-reads and updates.</summary>
    public void Refresh() => _context.RequestRefresh();

    /// <summary>Builds this instance's settings form, or null when it has none. The host wraps it in its own dialog chrome.</summary>
    public Control? CreateConfigView() => _configView?.Invoke(_context);
}
