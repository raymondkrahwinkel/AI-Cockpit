using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.Diagram;

// AC-847: what a coupled surface is doing right now — an agent pip, an operator pip, a "what's happening" line, a
// running change count. Same shared-registry shape as ActivityStrip (read that one first), and the same "absent,
// not empty-but-present" discipline: with nothing coupled this whole control collapses instead of showing idle pips.
internal sealed class PresenceIndicators : Border
{
    // How long a fresh non-operator edit counts as "writing" before the pip and the live line settle back to
    // "reading" — one clock drives both, so they can never disagree about whether an edit is still fresh.
    private static readonly TimeSpan WritingWindow = TimeSpan.FromSeconds(3);

    private readonly string _surfaceId;
    private readonly bool _whiteboard;
    private readonly IDiagramAccessRegistry? _diagramRegistry;
    private readonly IWhiteboardAccessRegistry? _whiteboardRegistry;
    private readonly int _baselineCount;
    private readonly Ellipse _agentPip = new() { Width = 8, Height = 8 };
    private readonly Ellipse _operatorPip = new() { Width = 8, Height = 8 };
    private readonly TextBlock _liveLine = new() { FontSize = 11 };
    private readonly TextBlock _counterLine = new() { FontSize = 10, Opacity = 0.6 };
    private string? _sessionName;
    private bool _coupled;
    private bool _agentHasCapability;
    private bool _operatorWriting;
    private bool _agentWriting;
    private string? _lastSummary;
    private int _writingGeneration;

    public PresenceIndicators(ICockpitHost host, string surfaceId, bool whiteboard)
    {
        _surfaceId = surfaceId;
        _whiteboard = whiteboard;
        _diagramRegistry = whiteboard ? null : host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _whiteboardRegistry = whiteboard ? host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry : null;

        // A window-session tally, not a lifetime one: whatever landed before this control existed is the baseline,
        // not part of "changed since I've been watching".
        _baselineCount = whiteboard
            ? _whiteboardRegistry?.History(_surfaceId).Count ?? 0
            : _diagramRegistry?.History(_surfaceId).Count ?? 0;

        ToolTip.SetTip(_agentPip, "Agent");
        ToolTip.SetTip(_operatorPip, "Jij");

        var pips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { _agentPip, _operatorPip },
        };

        Padding = new Thickness(12, 2, 12, 6);
        Child = new StackPanel { Spacing = 2, Children = { pips, _liveLine, _counterLine } };

        if (_diagramRegistry is not null)
        {
            _diagramRegistry.CouplingChanged += _OnDiagramCouplingChanged;
            _diagramRegistry.HistoryChanged += _OnHistoryChanged;
        }

        if (_whiteboardRegistry is not null)
        {
            _whiteboardRegistry.CouplingChanged += _OnWhiteboardCouplingChanged;
            _whiteboardRegistry.HistoryChanged += _OnHistoryChanged;
        }

        DetachedFromVisualTree += (_, _) =>
        {
            if (_diagramRegistry is not null)
            {
                _diagramRegistry.CouplingChanged -= _OnDiagramCouplingChanged;
                _diagramRegistry.HistoryChanged -= _OnHistoryChanged;
            }

            if (_whiteboardRegistry is not null)
            {
                _whiteboardRegistry.CouplingChanged -= _OnWhiteboardCouplingChanged;
                _whiteboardRegistry.HistoryChanged -= _OnHistoryChanged;
            }
        };

        _Refresh();
    }

    // Same pattern as ActivityStrip.SetSession — the workspace body already tracks the coupled session's display
    // name, this just gets told.
    public void SetSession(string? paneId, string? name)
    {
        _sessionName = name;
        _Refresh();
    }

    // The workspace body knows about its own operator-hold (_selected on the diagram; nothing on the whiteboard) —
    // this component does not, so it is told rather than reaching for it.
    public void SetOperatorWriting(bool writing)
    {
        _operatorWriting = writing;
        _Refresh();
    }

    private void _OnDiagramCouplingChanged(DiagramCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ApplyCoupling(change.Coupling is not null, change.Coupling?.CanRead ?? false));
    }

    private void _OnWhiteboardCouplingChanged(WhiteboardCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        Dispatcher.UIThread.Post(() => _ApplyCoupling(change.Coupling is not null, change.Coupling?.CanRead ?? false));
    }

    private void _ApplyCoupling(bool coupled, bool hasCapability)
    {
        _coupled = coupled;
        _agentHasCapability = hasCapability;
        _Refresh();
    }

    private void _OnHistoryChanged(string surfaceId)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            var last = _whiteboard
                ? _LastEntry(_whiteboardRegistry?.History(_surfaceId))
                : _LastEntry(_diagramRegistry?.History(_surfaceId));

            if (last is { Origin: not "operator", Reverted: false } entry)
            {
                _lastSummary = entry.Summary;
                _ = _PulseWritingAsync();
            }
            else
            {
                _Refresh();
            }
        });
    }

    private static (string Origin, string Summary, bool Reverted)? _LastEntry(IReadOnlyList<DiagramHistoryEntry>? entries) =>
        entries is { Count: > 0 } list ? (list[^1].Origin, list[^1].Summary, list[^1].Reverted) : null;

    private static (string Origin, string Summary, bool Reverted)? _LastEntry(IReadOnlyList<WhiteboardHistoryEntry>? entries) =>
        entries is { Count: > 0 } list ? (list[^1].Origin, list[^1].Summary, list[^1].Reverted) : null;

    // One shared clock: the pip colour and the live line never disagree about freshness, because both read this
    // flag. Generation-counter shape, like DiagramWorkspaceBody's own glow timer, so a rapid second edit restarts
    // the window rather than an older timer clearing a newer one.
    private async Task _PulseWritingAsync()
    {
        var myGeneration = ++_writingGeneration;
        _agentWriting = true;
        _Refresh();
        await Task.Delay(WritingWindow);
        if (_writingGeneration == myGeneration)
        {
            _agentWriting = false;
            _Refresh();
        }
    }

    private void _Refresh()
    {
        IsVisible = _coupled;
        if (!_coupled)
        {
            return;
        }

        _agentPip.Fill = _agentWriting ? _Brush("CockpitAccentBrush")
            : _agentHasCapability ? _Brush("CockpitStatusBusyBrush")
            : _Brush("CockpitTextSecondaryBrush");
        ToolTip.SetTip(_agentPip, _agentWriting ? "Agent bewerkt" : _agentHasCapability ? "Agent leest mee" : "Agent gekoppeld, geen rechten");

        _operatorPip.Fill = _operatorWriting ? _Brush("CockpitAccentBrush") : _Brush("CockpitTextSecondaryBrush");
        ToolTip.SetTip(_operatorPip, _operatorWriting ? "Jij bewerkt" : "Jij aanwezig");

        var name = _sessionName ?? "agent";
        // The coupling bar already says "2 tegelijk aan het werk" when both hold something — this is an additional,
        // distinctly worded line, not a duplicate of it.
        var both = _operatorWriting && (_agentWriting || _agentHasCapability) ? " · jij bewerkt ook" : "";
        _liveLine.Text = !_agentHasCapability && !_agentWriting
            ? $"{name} gekoppeld, nog niets gevraagd"
            : _agentWriting
                ? $"{name}: {_lastSummary}{both}"
                : $"{name} leest mee{both}";

        var total = _whiteboard ? _whiteboardRegistry?.History(_surfaceId).Count ?? 0 : _diagramRegistry?.History(_surfaceId).Count ?? 0;
        var delta = Math.Max(0, total - _baselineCount);
        _counterLine.Text = delta switch
        {
            0 => "0 wijzigingen",
            1 => "1 wijziging",
            _ => $"{delta} wijzigingen",
        };
    }

    private static IBrush? _Brush(string resourceKey) =>
        Application.Current?.FindResource(resourceKey) as IBrush;
}
