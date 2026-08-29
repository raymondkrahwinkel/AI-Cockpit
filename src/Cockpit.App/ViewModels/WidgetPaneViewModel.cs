using System.Text.Json;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using Material.Icons;
using Cockpit.App.Plugins;
using Cockpit.Core.Workspaces;
using Cockpit.Plugins.Abstractions.Widgets;

namespace Cockpit.App.ViewModels;

// AC-1013: One placed widget as the dashboard renders it: the pane's chrome (title, ↻, ⚙, ✕) plus the...
public sealed partial class WidgetPaneViewModel : ObservableObject
{
    private readonly WidgetContext _context;

    public WidgetPaneViewModel(WorkspacePane pane, WidgetRegistration registration, WidgetContext context)
    {
        _pane = pane;
        _context = context;
        Title = registration.Title;
        Icon = registration.Icon;
        IconKind = registration.IconKind;
        HasConfig = registration.HasConfig;
        View = registration.CreateView(context);
        _configView = registration.CreateConfigView;
    }

    private readonly Func<IWidgetContext, Control>? _configView;

    // Where this instance sits, and what it is. Settable so a move or resize updates the pane in place —
    // rebuilding it to change four numbers would throw away the plugin's control and everything it holds,
    // which is the mistake the session grid made on 2026-07-13.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Column), nameof(Row), nameof(ColumnSpan), nameof(RowSpan))]
    private WorkspacePane _pane;

    public string Id => Pane.Id;

    public string Title { get; }

    public string Icon { get; }

    // The bundled vector icon the widget named, or null when it only gave an `Icon` glyph.
    public MaterialIconKind? IconKind { get; }

    // Gates the pane header's ⚙: false means this widget declared no settings form, so no gear is shown at all.
    public bool HasConfig { get; }

    // The plugin's control for this instance, built once.
    public Control View { get; }

    public int Column => Pane.Cell.Column;

    public int Row => Pane.Cell.Row;

    public int ColumnSpan => Pane.Cell.ColumnSpan;

    public int RowSpan => Pane.Cell.RowSpan;

    // The ↻ on the pane header, and what a saved settings form raises — the widget re-reads and updates.
    public void Refresh() => _context.RequestRefresh();

    // Builds this instance's settings form, or null when it has none. The host wraps it in its own dialog chrome.
    public Control? CreateConfigView() => _configView?.Invoke(_context);

    // This instance's stored settings as raw JSON — what an export carries. The host never parses it: the shape is the plugin's business.
    public IReadOnlyDictionary<string, string> ReadConfig() =>
        _context.Storage is WidgetInstanceStorage storage ? storage.Snapshot() : new Dictionary<string, string>();

    // AC-1013: Writes settings from an import, then asks the widget to re-read them — which is how it...
    public void WriteConfig(IReadOnlyDictionary<string, JsonElement> config)
    {
        foreach (var (key, value) in config)
        {
            // Stored as the JSON it travelled as, so the widget deserialises exactly what it wrote.
            _context.Storage.Set(key, value);
        }

        Refresh();
    }
}
