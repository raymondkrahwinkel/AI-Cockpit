using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Consent;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugin.Diagram.Whiteboard.Model;
using Cockpit.Plugin.Diagram.Whiteboard.Rendering;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Notifications;
using Material.Icons.Avalonia;

namespace Cockpit.Plugin.Diagram.Whiteboard;

// A whiteboard as its own window beside the cockpit (AC-842), bound to a session that is already running — the
// whiteboard counterpart to DiagramWorkspaceBody (AC-834); read that one first. Deviation: the invite is a separate,
// visible ask (Couple never implies Grant, AC-810) and only ever offers read; write (AC-854) is asked by the agent.
internal sealed class WhiteboardWorkspaceBody : UserControl
{
    // AC-1007: 800x600 (a 1/3 scale of the 2400x1800 board) left a pasted screenshot's button captions — Cockpit's
    // own UI text sits around 12-13px — well under legible after two scale-downs. 1600x1200 (still 4:3, still a
    // fixed cap rather than the board's full resolution) keeps that same caption readable while staying far under
    // the megabytes an uncapped 2400x1800 PNG of a photographic paste could reach.
    private static readonly PixelSize SnapshotSize = new(1600, 1200);

    private readonly ICockpitHost _host;
    private readonly IWhiteboardAccessRegistry? _registry;
    private readonly IDiagramAccessRegistry? _diagrams;
    private readonly IWhiteboardSnapshotRenderer _renderer = new WhiteboardSnapshotRenderer();
    private readonly WhiteboardControl _control;
    private readonly string _surfaceId;
    private readonly string _documentTitle;
    private readonly Border _saveBar;
    private readonly Button _saveButton;
    private readonly TextBlock _saveStatus;
    private readonly Button _askButton;
    private readonly ActivityStrip _activityStrip;
    private readonly AskStrip _askStrip;
    private readonly PresenceIndicators _presence;
    private readonly Border _couplingBar;
    private readonly TextBlock _couplingLabel;
    private readonly TextBlock _readChip;
    private readonly TextBlock _editChip;
    private readonly MaterialIcon _pip;
    private readonly Button _coupleButton;
    private readonly Button _disconnectButton;
    private readonly Button _inviteButton;
    private readonly Button _convertButton;
    private readonly TextBlock _convertStatus;
    private string? _convertTarget;
    private bool _convertAsked;
    private int _proposals;
    private WhiteboardCoupling? _current;
    private SurfaceSessionBinding _sessionBinding;
    private string? _filePath;
    private string _savedText;
    private string? _fileAsLastSeen;

    public WhiteboardWorkspaceBody(ICockpitHost host, WhiteboardDocument document, string? sessionPaneId)
    {
        _host = host;
        _registry = host.Services.GetService(typeof(IWhiteboardAccessRegistry)) as IWhiteboardAccessRegistry;
        _diagrams = host.Services.GetService(typeof(IDiagramAccessRegistry)) as IDiagramAccessRegistry;
        _surfaceId = document.Id;
        _documentTitle = document.Title;
        _filePath = document.FilePath;
        _savedText = WhiteboardCatalog.Serialize(document);
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);
        _control = new WhiteboardControl(document);
        _control.Canvas.Changed += (_, _) =>
        {
            _registry?.UpdateSnapshot(_surfaceId, _Snapshot());
            _RefreshSaveBar();
        };

        // AC-912: Ctrl+Z refusing is a thing the operator has to be told about — a silently ignored key reads as
        // "undo is broken here", which is the very complaint this ticket came from.
        _control.Canvas.UndoRefused += reason => _host.ShowToast(reason, PluginToastSeverity.Warning);

        (_saveBar, _saveButton, _saveStatus, _askButton) = _BuildSaveBar();
        (_couplingBar, _couplingLabel, _readChip, _editChip, _pip, _coupleButton, _disconnectButton, _inviteButton) = _BuildCouplingBar();
        (var convertBar, _convertButton, _convertStatus) = _BuildConvertBar();
        var whiteboardJournal = new WhiteboardActivityJournal(_registry, _control.Canvas.Edits);
        _activityStrip = new ActivityStrip(host, _surfaceId, whiteboardJournal, key =>
        {
            if (Guid.TryParse(key, out var id))
            {
                _control.Canvas.SelectObject(id);
            }
        });
        _askStrip = new AskStrip(key =>
        {
            if (Guid.TryParse(key, out var id))
            {
                _control.Canvas.SelectObject(id);
            }
        });
        _presence = new PresenceIndicators(_surfaceId, whiteboardJournal, whiteboardJournal);
        _control.Canvas.SelectionChanged += (_, _) => _RefreshAskButton();
        _control.Canvas.ExtraContextMenuItems = _BuildAskContextMenuItems;

        Content = new DockPanel { Children = { _saveBar, _couplingBar, _presence, _askStrip, _activityStrip, convertBar, _control } };
        DockPanel.SetDock(_saveBar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_askStrip, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);
        DockPanel.SetDock(convertBar, Dock.Bottom);
        _RefreshSaveBar();

        // Bound before the first _RefreshAskButton: that reads _sessionBinding.IsLive for the ask button. The same
        // callback that refreshes the coupling bar on a change refreshes that button too.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, () => { _RefreshCouplingBar(); _RefreshAskButton(); });
        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _RefreshAskButton();

        if (_registry is not null)
        {
            // Subscribed before the surface is registered: a board an agent asked for (AC-835) arrives already
            // coupled, and that change is announced from inside SurfaceOpened.
            _registry.CouplingChanged += _OnCouplingChanged;
            _registry.ObjectPlaced += _OnObjectPlaced;
            _registry.ObjectErased += _OnObjectErased;
            _registry.HistoryChanged += _OnHistoryChanged;
            _registry.SurfaceOpened(_surfaceId, _documentTitle, _Snapshot());

            // A plain Couple — zero capabilities. The invite button (and read_whiteboard) still ask their own Grant.
            if (_sessionBinding.IsLive)
            {
                _registry.Couple(_sessionBinding.PaneId, _surfaceId);
            }
        }

        // AC-845: the statusregel does not take the agent's word for it — it counts proposals as they reach the
        // poort, so "1 omzetting voorgesteld" means one really landed there.
        if (_diagrams is not null)
        {
            _diagrams.ProposalChanged += _OnProposalChanged;
        }

        // No registry (an older host) means coupling cannot be shown or offered at all (AC-834's precedent).
        _couplingBar.IsVisible = _registry is not null;
        _RefreshCouplingBar();

        DetachedFromVisualTree += (_, _) =>
        {
            _sessionBinding.Dispose();
            if (_diagrams is not null)
            {
                _diagrams.ProposalChanged -= _OnProposalChanged;
            }

            if (_registry is null)
            {
                return;
            }

            _registry.CouplingChanged -= _OnCouplingChanged;
            _registry.ObjectPlaced -= _OnObjectPlaced;
            _registry.ObjectErased -= _OnObjectErased;
            _registry.HistoryChanged -= _OnHistoryChanged;
            _registry.SurfaceClosed(_surfaceId);
        };
    }

    // The way out of "window open, no agent" — after the bound session ended or the operator disconnected.
    private void _Recouple(string paneId)
    {
        if (_sessionBinding.Recouple(paneId, p => _registry?.Couple(p, _surfaceId)) is { } reason)
        {
            _host.ShowToast(reason, PluginToastSeverity.Error);
            return;
        }

        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _RefreshCouplingBar();
        _RefreshAskButton();
    }

    // AC-842's invite: a Grant *request*, not a silent Grant — the same Approve/Deny gate read_whiteboard uses,
    // just asked from the board instead of from the agent. The wording is Cockpit's, never the agent's.
    private async Task _InviteAsync()
    {
        if (_current is not { CanRead: false } || !_sessionBinding.IsLive)
        {
            return;
        }

        var paneId = _sessionBinding.PaneId;
        var decision = await _host.RequestConsentAsync(_InvitePrompt());
        if (!decision.IsApproved)
        {
            return;
        }

        try
        {
            _registry?.Couple(paneId, _surfaceId);
            _registry?.Grant(paneId, _surfaceId);
        }
        catch (InvalidOperationException exception)
        {
            _host.ShowToast(exception.Message, PluginToastSeverity.Error);
        }
    }

    // Names what is really shared, the same rule WhiteboardMcpTools._PromptFor follows for the agent-initiated ask.
    // AC-913: the board can be bigger than this window — the snapshot is always the whole board, scaled to fit,
    // never a crop of whatever happens to be in view, so the wording says "whole board", not "as it looks now".
    private ConsentRequest _InvitePrompt() =>
        new(
            "Let the agent look along on this whiteboard",
            $"Share a screenshot of the whole whiteboard ({SnapshotSize.Width}×{SnapshotSize.Height}, scaled to fit — not just the part of it visible in this window right now) with the session's agent — an image of the board, not its shapes or text as data. It cannot put anything on the board with this: drawing along is a separate question the agent has to ask for itself.",
            new ConsentSource(_surfaceId, null, ConsentSourceCatalog.WhiteboardInvite),
            "whiteboard.read",
            ConsentRisk.Dangerous);

    // W-4/AC-845: below the board, with its own status line — the button stays there even while disabled, since
    // "why can't I" is exactly what the operator needs to be able to read here.
    private (Border Bar, Button Convert, TextBlock Status) _BuildConvertBar()
    {
        var convert = new Button { Content = "Convert to diagram", Classes = { "Compact" } };
        convert.Click += (_, _) => _ShowConvertMenu(convert);
        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };

        var bar = new Border
        {
            Padding = new Thickness(8, 4),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { convert, status } },
        };
        return (bar, convert, status);
    }

    // Two answers (AC-845): convert — to a diagram that is already open, or to a new window — or just write it
    // down. Where it goes is asked, never guessed (AC-812's rule).
    private void _ShowConvertMenu(Control anchor)
    {
        var flyout = new MenuFlyout();
        var fresh = new MenuItem { Header = "Convert to a new diagram" };
        fresh.Click += (_, _) => _ConvertToNew();
        flyout.Items.Add(fresh);

        // A diagram another agent is already holding would be a dead-end choice: edit_diagram refuses there.
        foreach (var surface in _diagrams?.ListSurfaces(_sessionBinding.PaneId).Where(s => !_diagrams.IsCoupledByAnother(_sessionBinding.PaneId, s.SurfaceId)) ?? [])
        {
            var item = new MenuItem { Header = $"Convert to \"{surface.Name}\"" };
            item.Click += (_, _) => _Convert(surface.SurfaceId, surface.Name);
            flyout.Items.Add(item);
        }

        var writeDown = new MenuItem { Header = "Just write it down" };
        writeDown.Click += (_, _) => _Ask(WhiteboardToDiagram.WriteDownPrompt(_documentTitle), target: null);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(writeDown);
        flyout.ShowAt(anchor);
    }

    // A conversion with no target opens one itself: empty, so this path also goes through the diff gate rather
    // than arriving with a finished diagram.
    private void _ConvertToNew()
    {
        var document = DiagramDocument.New($"{_documentTitle} — diagram");
        _ = DiagramWindow.OpenAsync(_host, document, _sessionBinding.LivePaneId);
        _Convert(document.Id, document.Title);
    }

    private void _Convert(string surfaceId, string name) =>
        _Ask(WhiteboardToDiagram.ConvertPrompt(_documentTitle, surfaceId, name), surfaceId);

    private void _Ask(string prompt, string? target)
    {
        _convertTarget = target;
        _convertAsked = target is not null;
        _proposals = 0;
        _RefreshConvertBar();
        _ = _sessionBinding.SendAsync(prompt);
    }

    // Only a proposal on the diagram this conversion actually went to counts — any other proposal is about work
    // that has nothing to do with this board.
    private void _OnProposalChanged(string surfaceId, DiagramProposal? proposal)
    {
        if (surfaceId != _convertTarget || proposal is null)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            _proposals++;
            _RefreshConvertBar();
        });
    }

    private void _RefreshConvertBar()
    {
        var blocker = WhiteboardToDiagram.Blocker(_diagrams is not null, _sessionBinding.IsLive, _current);
        _convertButton.IsEnabled = blocker is null;
        _convertStatus.Text = blocker ?? WhiteboardToDiagram.Status(_convertAsked, _proposals);
    }

    private byte[] _Snapshot()
    {
        using var bitmap = _renderer.Render(_control.Canvas.Document, SnapshotSize);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    // W-2/AC-843: the status bar — same shape as DiagramWorkspaceBody's save bar (AC-839), one Save button plus
    // where-it-landed text. AC-910's "Ask the agent…" sits beside it — the operator's free-text ask about whatever
    // is selected, or the board as a whole, see _AddAsk.
    private (Border Bar, Button Save, TextBlock Status, Button Ask) _BuildSaveBar()
    {
        var save = new Button { Content = "Save", Classes = { "Compact" } };
        save.Click += (_, _) => _ = _SaveAsync();
        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        var ask = new Button { Content = "Ask the agent…", Classes = { "Compact" } };
        ask.Click += (_, _) => _AddAsk(ask);

        var bar = new Border
        {
            Padding = new Thickness(8, 4),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { save, status, ask } },
        };
        return (bar, save, status, ask);
    }

    // AC-910: asking works on the selection or on the board as a whole (criterion 7), so the only real gate is a
    // live coupled session — same "explain at the point of use" rule as DiagramWorkspaceBody's ask button.
    private void _RefreshAskButton()
    {
        _askButton.IsEnabled = _sessionBinding.IsLive;
        ToolTip.SetTip(
            _askButton,
            _sessionBinding.IsLive ? "Ask the agent about the selected object, or the whole board."
            : "Couple a conversation first (\"Couple…\" above) to be able to ask the agent.");
    }

    // AC-910: asks the coupled session about whatever is selected, or the board as a whole with nothing selected —
    // the shared flyout/message/strip, this surface's own descriptor (kind + text + board-pixel rect, never a Guid:
    // read_whiteboard gives back a PNG, not shapes/strokes as data, so an id here would be noise to the agent).
    private void _AddAsk(Control anchor)
    {
        if (!_sessionBinding.IsLive)
        {
            return;
        }

        var objectKey = _control.Canvas.SelectedId?.ToString();
        var context = new AskContext("whiteboard", _surfaceId, _documentTitle, ObjectRef: null, _SelectedObjectLabel());
        AskFlyout.Show(anchor, "What should the agent do here?", question =>
        {
            _askStrip.Add(question, objectKey);
            _ = _sessionBinding.SendAsync(AskMessage.Compose(context, question));
        });
    }

    // AC-924: the "Ask the agent…" entry for the board's own object menu — built fresh every time that menu opens
    // (WhiteboardCanvasControl's ExtraContextMenuItems hook), and, per AC-703, posted onto the dispatcher with the
    // save bar's own ask button as anchor so it never races the menu's own close.
    private IEnumerable<Control> _BuildAskContextMenuItems()
    {
        var ask = new MenuItem { Header = "Ask the agent…", IsEnabled = _sessionBinding.IsLive };
        if (!_sessionBinding.IsLive)
        {
            ToolTip.SetTip(ask, "Couple a conversation first (\"Couple…\" above) to be able to ask the agent.");
        }

        ask.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => _AddAsk(_askButton));
        return [ask];
    }

    private string? _SelectedObjectLabel()
    {
        if (_control.Canvas.SelectedId is not { } id || _control.Canvas.Document.Find(id) is not { } selected)
        {
            return null;
        }

        var bounds = WhiteboardGeometry.BoundsOf(selected);
        var text = selected is PlacedObject { Text: { } placedText } ? $" reading \"{placedText}\"" : "";
        var kind = selected is PlacedObject placed ? placed.ShapeKind.ToString() : "freehand stroke";
        return $"{kind}{text} around ({bounds.X:0}, {bounds.Y:0}), {bounds.Width:0}×{bounds.Height:0}";
    }

    // One save path (AC-839's precedent): a hand-drawn stroke, a placed shape, a pasted image and an agent
    // placement (AC-854) all arrive through Document.Objects, so this is the same save for all four.
    private async Task _SaveAsync()
    {
        if (_filePath is { } existing)
        {
            _Persist(() =>
            {
                WhiteboardCatalog.Write(existing, _control.Canvas.Document, _fileAsLastSeen);
                return existing;
            });
            return;
        }

        var homes = WhiteboardCatalog.WritableHomes(await _host.GetProjectMemoryRowsAsync(_sessionBinding.LivePaneId));
        if (homes.Count == 0)
        {
            _host.ShowToast(
                "This project has no memory path — add one in the project editor before saving a whiteboard.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(() => WhiteboardCatalog.Create(homes[0].Reference, _control.Canvas.Document));
            return;
        }

        // More than one memory path: ask, don't guess (AC-812). The answer stays with this board.
        var flyout = new MenuFlyout();
        foreach (var home in homes)
        {
            var item = new MenuItem { Header = home.Label ?? home.Reference };
            item.Click += (_, _) => _Persist(() => WhiteboardCatalog.Create(home.Reference, _control.Canvas.Document));
            flyout.Items.Add(item);
        }

        flyout.ShowAt(_saveButton);
    }

    private void _Persist(Func<string> write)
    {
        try
        {
            _filePath = write();
        }
        catch (Exception exception)
        {
            _host.ShowToast($"Save failed: {exception.Message}", PluginToastSeverity.Error);
            return;
        }

        _savedText = WhiteboardCatalog.Serialize(_control.Canvas.Document);
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);
        _RefreshSaveBar();
    }

    private void _RefreshSaveBar()
    {
        var dirty = WhiteboardCatalog.Serialize(_control.Canvas.Document) != _savedText;
        var where = _filePath ?? "No file yet";
        _saveStatus.Text = dirty ? $"{where} · unsaved changes" : where;
        ToolTip.SetTip(_saveStatus, _filePath);
        _saveButton.IsEnabled = dirty || _filePath is null;
    }

    // An agent's object arriving over MCP (AC-854): it lands in the same document the operator draws in, marked as
    // the agent's so it is drawn and saved as such, and the registry's snapshot is refreshed so the next read of the
    // board shows it. Nothing already on the board is touched.
    private void _OnObjectPlaced(string surfaceId, string objectId, WhiteboardPlacement placement)
    {
        if (surfaceId != _surfaceId || !Enum.TryParse<PlacedShapeKind>(placement.Shape, ignoreCase: true, out var kind) || kind == PlacedShapeKind.Image)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var document = _control.Canvas.Document;

            // W-6/AC-851: an agent's placement binds to a pasted image underneath it exactly like the operator's
            // own strokes and shapes do — same geometric rule, no separate parameter the agent has to pass.
            var parentId = WhiteboardBinding.FindParentImage(
                document, placement.X + (placement.Width / 2), placement.Y + (placement.Height / 2))?.Id;

            document.Add(new PlacedObject
            {
                Id = Guid.TryParse(objectId, out var id) ? id : Guid.NewGuid(),
                ShapeKind = kind,
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                Text = placement.Text,
                PlacedByAgent = true,
                ParentImageId = parentId,
            });
            _registry?.UpdateSnapshot(_surfaceId, _Snapshot());
            _RefreshSaveBar();
        });
    }

    // Only ever reaches an object the agent placed itself — the registry refuses the rest before it gets here.
    private void _OnObjectErased(string surfaceId, string objectId)
    {
        if (surfaceId != _surfaceId || !Guid.TryParse(objectId, out var id))
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_control.Canvas.Document.Remove(id))
            {
                _registry?.UpdateSnapshot(_surfaceId, _Snapshot());
                _RefreshSaveBar();
            }
        });
    }

    // AC-847: no zoom/pan and no per-object bounds map here, so the diagram's cursor/glow/follow collapse into one
    // call — selecting the object is itself the highlight (WhiteboardCanvasControl.SelectObject), and there is no
    // camera to move so "bring the operator to it" needs nothing more than that.
    private void _OnHistoryChanged(string surfaceId)
    {
        if (surfaceId != _surfaceId)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var last = _registry?.History(_surfaceId).LastOrDefault();
            if (last is { Origin: not "operator", Reverted: false } entry && Guid.TryParse(entry.ObjectId, out var id))
            {
                _control.Canvas.SelectObject(id);
            }
        });
    }

    private void _OnCouplingChanged(WhiteboardCouplingChange change)
    {
        if (change.SurfaceId != _surfaceId)
        {
            return;
        }

        _current = change.Coupling;
        Avalonia.Threading.Dispatcher.UIThread.Post(_RefreshCouplingBar);
    }

    // The "agent connected" bar (AC-810/AC-824/AC-834's precedent), always on screen: "no agent on this board" is
    // a real state — after the session ended, or after Disconnect — not one the bar should hide from.
    private (Border Bar, TextBlock Label, TextBlock ReadChip, TextBlock EditChip, MaterialIcon Pip, Button Couple, Button Disconnect, Button Invite) _BuildCouplingBar()
    {
        var invite = new Button { Content = "Let the agent look along", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
        invite.Click += (_, _) => _ = _InviteAsync();

        var parts = CouplingBarFactory.Build(_documentTitle, extraActions: [invite]);
        parts.Disconnect.Click += (_, _) => _registry?.Disconnect(_surfaceId);
        parts.Couple.Click += (_, _) => _ShowSessionPicker(parts.Couple);

        return (parts.Bar, parts.Label, parts.ReadChip, parts.EditChip, parts.Pip, parts.Couple, parts.Disconnect, invite);
    }

    private void _ShowSessionPicker(Control anchor) => _sessionBinding.ShowSessionPicker(anchor, _Recouple);

    private void _RefreshCouplingBar()
    {
        _RefreshConvertBar();
        var coupled = _current is not null;
        _disconnectButton.IsVisible = coupled;
        _coupleButton.IsVisible = !coupled;
        _inviteButton.IsVisible = coupled && _current is { CanRead: false };
        _readChip.IsVisible = coupled;
        _editChip.IsVisible = coupled;

        if (_current is not { } coupling)
        {
            _couplingLabel.Text = _sessionBinding.EndedSessionName is { } ended
                ? $"Session {ended} has ended — this window stays open."
                : "No agent coupled.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            _pip.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _sessionBinding.DisplayName ?? coupling.SessionId;
        var readAt = coupling.LastReadAt is { } at ? $" · read {at.ToLocalTime():HH:mm}" : "";
        _couplingLabel.Text = coupling.CanRead
            ? $"Agent connected — session {name}{readAt}"
            : $"Agent connected — session {name} (no capabilities granted yet)";
        _couplingLabel.Foreground = _Brush("CockpitAccentBrush");
        _pip.Foreground = coupling.CanRead ? _Brush("CockpitAccentBrush") : _Brush("CockpitTextSecondaryBrush");
        SurfaceChrome.SetChip(_readChip, "read_whiteboard", coupling.CanRead);
        SurfaceChrome.SetChip(_editChip, "place_on_whiteboard", coupling.CanWrite);
    }

    private static IBrush? _Brush(string resourceKey) => SurfaceChrome.Brush(resourceKey);
}
