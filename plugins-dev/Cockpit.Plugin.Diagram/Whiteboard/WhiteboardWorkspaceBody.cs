using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
    private static readonly PixelSize SnapshotSize = new(800, 600);

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
    private readonly Button _pinButton;
    private readonly ActivityStrip _activityStrip;
    private readonly PinStrip _pinStrip;
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

        (_saveBar, _saveButton, _saveStatus, _pinButton) = _BuildSaveBar();
        (_couplingBar, _couplingLabel, _readChip, _editChip, _pip, _coupleButton, _disconnectButton, _inviteButton) = _BuildCouplingBar();
        (var convertBar, _convertButton, _convertStatus) = _BuildConvertBar();
        _activityStrip = new ActivityStrip(host, _surfaceId, new WhiteboardActivityJournal(_registry), key =>
        {
            if (Guid.TryParse(key, out var id))
            {
                _control.Canvas.SelectObject(id);
            }
        });
        _pinStrip = new PinStrip(host, _surfaceId, whiteboard: true, key =>
        {
            if (Guid.TryParse(key, out var id))
            {
                _control.Canvas.SelectObject(id);
            }
        });
        _presence = new PresenceIndicators(host, _surfaceId, whiteboard: true);
        _control.Canvas.SelectionChanged += (_, _) => _RefreshPinButton();

        Content = new DockPanel { Children = { _saveBar, _couplingBar, _presence, _pinStrip, _activityStrip, convertBar, _control } };
        DockPanel.SetDock(_saveBar, Dock.Top);
        DockPanel.SetDock(_couplingBar, Dock.Top);
        DockPanel.SetDock(_presence, Dock.Top);
        DockPanel.SetDock(_pinStrip, Dock.Bottom);
        DockPanel.SetDock(_activityStrip, Dock.Bottom);
        DockPanel.SetDock(convertBar, Dock.Bottom);
        _RefreshSaveBar();

        // Bound before the first _RefreshPinButton (AC-849): that reads _sessionBinding.IsLive for the pin button.
        // The same callback that refreshes the coupling bar on a change refreshes that button too.
        _sessionBinding = new SurfaceSessionBinding(host, sessionPaneId, () => { _RefreshCouplingBar(); _RefreshPinButton(); });
        _activityStrip.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _presence.SetSession(_sessionBinding.LivePaneId, _sessionBinding.BoundSessionName);
        _RefreshPinButton();

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
        _RefreshPinButton();
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
    private ConsentRequest _InvitePrompt() =>
        new(
            "Let the agent look along on this whiteboard",
            $"Share a screenshot of this whiteboard ({SnapshotSize.Width}×{SnapshotSize.Height}) with the session's agent, exactly as it looks right now — an image of the board, not its shapes or text as data. It cannot put anything on the board with this: drawing along is a separate question the agent has to ask for itself.",
            new ConsentSource(_surfaceId, null, ConsentSourceCatalog.WhiteboardInvite),
            "whiteboard.read",
            ConsentRisk.Dangerous);

    // W-4/AC-845: onder het bord, met zijn eigen statusregel — de knop staat er ook als hij uit is, want "waarom
    // kan dit niet" is precies wat de operator hier moet kunnen lezen.
    private (Border Bar, Button Convert, TextBlock Status) _BuildConvertBar()
    {
        var convert = new Button { Content = "Naar diagram omzetten", Classes = { "Compact" } };
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

    // Twee antwoorden (AC-845): omzetten — naar een diagram dat al openstaat, of naar een nieuw venster — of alleen
    // opschrijven. Waar het heen gaat wordt gevraagd, niet geraden (AC-812's regel).
    private void _ShowConvertMenu(Control anchor)
    {
        var flyout = new MenuFlyout();
        var fresh = new MenuItem { Header = "Omzetten naar een nieuw diagram" };
        fresh.Click += (_, _) => _ConvertToNew();
        flyout.Items.Add(fresh);

        // Een diagram dat een andere agent vasthoudt zou een doodlopende keuze zijn: edit_diagram weigert daar.
        foreach (var surface in _diagrams?.ListSurfaces(_sessionBinding.PaneId).Where(s => !_diagrams.IsCoupledByAnother(_sessionBinding.PaneId, s.SurfaceId)) ?? [])
        {
            var item = new MenuItem { Header = $"Omzetten naar \"{surface.Name}\"" };
            item.Click += (_, _) => _Convert(surface.SurfaceId, surface.Name);
            flyout.Items.Add(item);
        }

        var writeDown = new MenuItem { Header = "Alleen opschrijven" };
        writeDown.Click += (_, _) => _Ask(WhiteboardToDiagram.WriteDownPrompt(_documentTitle), target: null);
        flyout.Items.Add(new Separator());
        flyout.Items.Add(writeDown);
        flyout.ShowAt(anchor);
    }

    // Een omzetting zonder doel opent er zelf een: leeg, zodat ook dit pad door de diff-poort gaat in plaats van
    // met een klaar diagram binnen te komen.
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

    // Alleen een voorstel op hét diagram waar deze omzetting heen ging telt: elk ander voorstel gaat over werk dat
    // niets met dit bord te maken heeft.
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

    // W-2/AC-843: the statusregel — same shape as DiagramWorkspaceBody's save bar (AC-839), one Opslaan button plus
    // where-it-landed text. AC-849's Prikken sits beside it — the operator's question about whatever is selected,
    // sent to the coupled session as a "📍 pin N" reference, see _AddPin.
    private (Border Bar, Button Save, TextBlock Status, Button Pin) _BuildSaveBar()
    {
        var save = new Button { Content = "Opslaan", Classes = { "Compact" } };
        save.Click += (_, _) => _ = _SaveAsync();
        var status = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 11,
            MaxWidth = 320,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = _Brush("CockpitTextSecondaryBrush"),
        };
        var pin = new Button { Content = "Prikken", Classes = { "Compact" } };
        pin.Click += (_, _) => _AddPin(pin);

        var bar = new Border
        {
            Padding = new Thickness(8, 4),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { save, status, pin } },
        };
        return (bar, save, status, pin);
    }

    // AC-849: prikken needs both a selected object and a live session to send the reference to — same "explain at
    // the point of use" rule as DiagramWorkspaceBody._RefreshHandEditBar's pin button.
    private void _RefreshPinButton()
    {
        var selected = _control.Canvas.SelectedId is not null;
        _pinButton.IsEnabled = _registry is not null && _sessionBinding.IsLive && selected;
        ToolTip.SetTip(
            _pinButton,
            !_sessionBinding.IsLive ? "Koppel eerst een gesprek om te kunnen prikken."
            : !selected ? "Selecteer eerst een object om te prikken."
            : "Prik een vraag op dit object.");
    }

    // AC-849: plants a pin on the selected object and sends its "📍 pin N" reference to the coupled session right
    // away — same fire-and-forget SendAsync as _Ask's convert prompt, since a pin's whole point is landing as a
    // chat message, not living only on this board.
    private void _AddPin(Control anchor)
    {
        if (_registry is null || !_sessionBinding.IsLive || _control.Canvas.SelectedId is not { } id)
        {
            return;
        }

        var label = _control.Canvas.Document.Find(id) is PlacedObject placed ? placed.Text ?? placed.ShapeKind.ToString() : null;
        var question = new TextBox { Width = 260, PlaceholderText = "Waar twijfel je over?" };
        var confirm = new Button { Content = "Prikken", Classes = { "Compact" }, HorizontalAlignment = HorizontalAlignment.Right };
        var flyout = new Flyout
        {
            Content = new StackPanel { Spacing = 8, Margin = new Thickness(12), Children = { question, confirm } },
        };

        void Plant()
        {
            var text = question.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            flyout.Hide();
            _registry.AddPin(_surfaceId, id.ToString(), text);
            var index = _registry.Pins(_surfaceId).Count;
            _ = _sessionBinding.SendAsync(PinMessage.Compose(_documentTitle, index, label, text));
        }

        confirm.Click += (_, _) => Plant();
        question.KeyDown += (_, key) =>
        {
            if (key.Key == Key.Enter)
            {
                key.Handled = true;
                Plant();
            }
        };

        flyout.ShowAt(anchor);
        question.Focus();
    }

    // Eén opslagweg (AC-839's precedent): een hand-tekening, een neergezette vorm, een plakte afbeelding en een
    // agent-plaatsing (AC-854) komen allemaal via Document.Objects binnen, dus dit is voor alle vier dezelfde save.
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
                "Dit project heeft geen geheugenpad — voeg er een toe in de projecteditor voordat je een whiteboard opslaat.",
                PluginToastSeverity.Warning);
            return;
        }

        if (homes.Count == 1)
        {
            _Persist(() => WhiteboardCatalog.Create(homes[0].Reference, _control.Canvas.Document));
            return;
        }

        // Meer dan één geheugenpad: vragen, niet kiezen (AC-812). Het antwoord blijft bij dít bord.
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
            _host.ShowToast($"Opslaan is niet gelukt: {exception.Message}", PluginToastSeverity.Error);
            return;
        }

        _savedText = WhiteboardCatalog.Serialize(_control.Canvas.Document);
        _fileAsLastSeen = SurfaceChrome.ReadFile(_filePath);
        _RefreshSaveBar();
    }

    private void _RefreshSaveBar()
    {
        var dirty = WhiteboardCatalog.Serialize(_control.Canvas.Document) != _savedText;
        var where = _filePath ?? "Nog geen bestand";
        _saveStatus.Text = dirty ? $"{where} · onbewaarde wijzigingen" : where;
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
            _control.Canvas.Document.Add(new PlacedObject
            {
                Id = Guid.TryParse(objectId, out var id) ? id : Guid.NewGuid(),
                ShapeKind = kind,
                X = placement.X,
                Y = placement.Y,
                Width = placement.Width,
                Height = placement.Height,
                Text = placement.Text,
                PlacedByAgent = true,
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
        var invite = new Button { Content = "Laat sdk meekijken", Classes = { "Compact" }, VerticalAlignment = VerticalAlignment.Center };
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
                ? $"Sessie {ended} is afgelopen — dit venster blijft open."
                : "Geen agent gekoppeld.";
            _couplingLabel.Foreground = _Brush("CockpitTextSecondaryBrush");
            _pip.Foreground = _Brush("CockpitTextSecondaryBrush");
            return;
        }

        var name = _sessionBinding.DisplayName ?? coupling.SessionId;
        var readAt = coupling.LastReadAt is { } at ? $" · gelezen {at.ToLocalTime():HH:mm}" : "";
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
