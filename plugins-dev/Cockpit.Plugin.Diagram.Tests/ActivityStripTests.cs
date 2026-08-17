using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.Diagram.Tests;

[Collection("avalonia")]
public class ActivityStripTests
{
    // Only the members ActivityStrip actually calls (History/HistoryChanged/Revert) do anything; everything else
    // on the interface is a no-op stand-in, the same shape FakeHost below takes for ICockpitHost. Internal, not
    // private: PresenceIndicatorsTests (AC-847) reuses this rather than writing a second fake for the same interface.
    internal sealed class FakeDiagramRegistry : IDiagramAccessRegistry
    {
        private readonly Dictionary<string, List<DiagramHistoryEntry>> _history = new();
        private readonly Dictionary<string, List<DiagramPin>> _pins = new();

        public List<string> RevertCalls { get; } = [];

        public string? NextRevertRefusal { get; set; }

        public event Action<string, string>? TextChanged { add { } remove { } }

        // PresenceIndicators (AC-847) needs a real CouplingChanged, unlike ActivityStrip which never subscribes to
        // it — a no-op stand-in here would mean its tests could never see a coupling appear or drop.
        public event Action<DiagramCouplingChange>? CouplingChanged;

        public event Action<string, DiagramProposal?>? ProposalChanged { add { } remove { } }

        public event Action<string, string>? ObjectEdited { add { } remove { } }

        public event Action<string>? HistoryChanged;

        // PinStrip (AC-849) needs a real PinsChanged, the same reason CouplingChanged above is real.
        public event Action<string>? PinsChanged;

        public void Seed(string surfaceId, DiagramHistoryEntry entry) =>
            (_history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = []).Add(entry);

        public void Seed(string surfaceId, DiagramPin pin) =>
            (_pins.TryGetValue(surfaceId, out var list) ? list : _pins[surfaceId] = []).Add(pin);

        public void Raise(string surfaceId) => HistoryChanged?.Invoke(surfaceId);

        public void SetCoupling(string surfaceId, DiagramCoupling? coupling) =>
            CouplingChanged?.Invoke(new DiagramCouplingChange(surfaceId, coupling));

        public IReadOnlyList<DiagramHistoryEntry> History(string surfaceId) =>
            _history.TryGetValue(surfaceId, out var list) ? list : [];

        public string? Revert(string surfaceId, string entryId)
        {
            RevertCalls.Add(entryId);
            if (NextRevertRefusal is { } reason)
            {
                return reason;
            }

            var list = _history[surfaceId];
            var index = list.FindIndex(entry => entry.Id == entryId);
            list[index] = list[index] with { Reverted = true };
            HistoryChanged?.Invoke(surfaceId);
            return null;
        }

        public void SurfaceOpened(string surfaceId, string name, string initialText)
        {
        }

        public void SurfaceClosed(string surfaceId)
        {
        }

        public void UpdateText(string surfaceId, string text)
        {
        }

        public void Disconnect(string surfaceId)
        {
        }

        public string? PeekText(string surfaceId) => null;

        public IReadOnlyList<DiagramSurfaceView> ListSurfaces(string sessionId) => [];

        public DiagramSurface? Resolve(string surfaceRef) => null;

        public DiagramCoupling? CouplingOf(string sessionId, string surfaceId) => null;

        public bool IsCoupledByAnother(string sessionId, string surfaceId) => false;

        public void Couple(string sessionId, string surfaceId)
        {
        }

        public void Grant(string sessionId, string surfaceId, DiagramCapability capability)
        {
        }

        public string? ReadCoupled(string sessionId, string surfaceId) => null;

        public bool WriteCoupled(string sessionId, string surfaceId, string text) => false;

        public bool EditCoupled(string sessionId, string surfaceId, DiagramHandEditKind kind, string objectKey, Func<string, (string? Text, string Summary)> edit) => false;

        public string? ApplyHandEdit(string surfaceId, DiagramHandEdit edit) => null;

        public void HoldObject(string surfaceId, string objectId)
        {
        }

        public void ReleaseObject(string surfaceId, string objectId)
        {
        }

        public bool IsHeldByOperator(string surfaceId, string objectId) => false;

        public void SessionEnded(string sessionId)
        {
        }

        public bool Propose(string sessionId, string surfaceId, string proposedText, string changeSummary, IReadOnlyList<string> fidelityFindings) => false;

        public DiagramProposal? PendingProposal(string surfaceId) => null;

        public bool ResolveProposal(string surfaceId, IReadOnlySet<int> acceptedBlocks) => false;

        public bool DiscardProposal(string surfaceId) => false;

        public event Action<DiagramOpenRequest>? OpenRequested { add { } remove { } }

        public bool RequestOpen(DiagramOpenRequest request) => false;

        public IReadOnlyList<DiagramPin> Pins(string surfaceId) =>
            _pins.TryGetValue(surfaceId, out var list) ? list : [];

        public string AddPin(string surfaceId, string objectKey, string question)
        {
            var pin = new DiagramPin(Guid.NewGuid().ToString("N"), objectKey, question, DateTime.Now, Closed: false);
            Seed(surfaceId, pin);
            PinsChanged?.Invoke(surfaceId);
            return pin.Id;
        }

        public void ClosePin(string surfaceId, string pinId)
        {
            if (!_pins.TryGetValue(surfaceId, out var list))
            {
                return;
            }

            var index = list.FindIndex(pin => pin.Id == pinId);
            if (index < 0)
            {
                return;
            }

            list[index] = list[index] with { Closed = true };
            PinsChanged?.Invoke(surfaceId);
        }
    }

    internal sealed class FakeWhiteboardRegistry : IWhiteboardAccessRegistry
    {
        private readonly Dictionary<string, List<WhiteboardHistoryEntry>> _history = new();
        private readonly Dictionary<string, List<WhiteboardPin>> _pins = new();

        public List<string> RevertCalls { get; } = [];

        public event Action<string, byte[]>? SnapshotChanged { add { } remove { } }

        // Same reason as FakeDiagramRegistry's CouplingChanged above: PresenceIndicators actually subscribes.
        public event Action<WhiteboardCouplingChange>? CouplingChanged;

        public event Action<string, string, WhiteboardPlacement>? ObjectPlaced { add { } remove { } }

        public event Action<string, string>? ObjectErased { add { } remove { } }

        public event Action<string>? HistoryChanged;

        // Same reason as FakeDiagramRegistry's PinsChanged above: PinStrip actually subscribes.
        public event Action<string>? PinsChanged;

        public void Seed(string surfaceId, WhiteboardHistoryEntry entry) =>
            (_history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = []).Add(entry);

        public void Seed(string surfaceId, WhiteboardPin pin) =>
            (_pins.TryGetValue(surfaceId, out var list) ? list : _pins[surfaceId] = []).Add(pin);

        public void Raise(string surfaceId) => HistoryChanged?.Invoke(surfaceId);

        public void SetCoupling(string surfaceId, WhiteboardCoupling? coupling) =>
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));

        public IReadOnlyList<WhiteboardHistoryEntry> History(string surfaceId) =>
            _history.TryGetValue(surfaceId, out var list) ? list : [];

        public string? Revert(string surfaceId, string entryId)
        {
            RevertCalls.Add(entryId);
            return "Het terughalen van een verwijderd object kan nog niet worden teruggedraaid.";
        }

        public void SurfaceOpened(string surfaceId, string name, byte[] initialSnapshotPng)
        {
        }

        public void SurfaceClosed(string surfaceId)
        {
        }

        public void UpdateSnapshot(string surfaceId, byte[] snapshotPng)
        {
        }

        public void Disconnect(string surfaceId)
        {
        }

        public byte[]? PeekSnapshot(string surfaceId) => null;

        public IReadOnlyList<WhiteboardSurfaceView> ListSurfaces(string sessionId) => [];

        public WhiteboardSurface? Resolve(string surfaceRef) => null;

        public WhiteboardCoupling? CouplingOf(string sessionId, string surfaceId) => null;

        public bool IsCoupledByAnother(string sessionId, string surfaceId) => false;

        public void Couple(string sessionId, string surfaceId)
        {
        }

        public void Grant(string sessionId, string surfaceId, WhiteboardCapability capability = WhiteboardCapability.Read)
        {
        }

        public byte[]? ReadCoupled(string sessionId, string surfaceId) => null;

        public string? PlaceCoupled(string sessionId, string surfaceId, WhiteboardPlacement placement) => null;

        public bool ErasePlaced(string sessionId, string surfaceId, string objectId) => false;

        public void MarkRead(string sessionId, string surfaceId)
        {
        }

        public void SessionEnded(string sessionId)
        {
        }

        public event Action<WhiteboardOpenRequest>? OpenRequested { add { } remove { } }

        public bool RequestOpen(WhiteboardOpenRequest request) => false;

        public IReadOnlyList<WhiteboardPin> Pins(string surfaceId) =>
            _pins.TryGetValue(surfaceId, out var list) ? list : [];

        public string AddPin(string surfaceId, string objectId, string question)
        {
            var pin = new WhiteboardPin(Guid.NewGuid().ToString("N"), objectId, question, DateTime.Now, Closed: false);
            Seed(surfaceId, pin);
            PinsChanged?.Invoke(surfaceId);
            return pin.Id;
        }

        public void ClosePin(string surfaceId, string pinId)
        {
            if (!_pins.TryGetValue(surfaceId, out var list))
            {
                return;
            }

            var index = list.FindIndex(pin => pin.Id == pinId);
            if (index < 0)
            {
                return;
            }

            list[index] = list[index] with { Closed = true };
            PinsChanged?.Invoke(surfaceId);
        }
    }

    internal sealed class FakeHost : ICockpitHost
    {
        public FakeHost(FakeDiagramRegistry? diagram = null, FakeWhiteboardRegistry? whiteboard = null)
        {
            Services = new FakeServices(diagram, whiteboard);
        }

        public List<string> Toasts { get; } = [];

        public IServiceProvider Services { get; }

        public ICockpitActions Actions => throw new NotSupportedException();

        public IPluginStorage Storage => throw new NotSupportedException();

        public ICockpitSessionObserver Sessions => throw new NotSupportedException();

        public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
            Toasts.Add(message);

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;

        private sealed class FakeServices(FakeDiagramRegistry? diagram, FakeWhiteboardRegistry? whiteboard) : IServiceProvider
        {
            public object? GetService(Type serviceType) =>
                serviceType == typeof(IDiagramAccessRegistry) ? diagram :
                serviceType == typeof(IWhiteboardAccessRegistry) ? whiteboard :
                null;
        }
    }

    // A strip's ScrollViewer is a templated control — its content only joins the visual tree once the template
    // applies, which needs a rooted window (same reason DiagramCollabWindowTests shows its content before
    // walking GetVisualDescendants).
    private static Window _Show(Control content)
    {
        var window = new Window { Content = content, Width = 400, Height = 300 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static List<string?> _Texts(Control content) =>
        content.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsVisible).Select(t => t.Text).ToList();

    private static DiagramHistoryEntry DiagramEntry(string id, string origin, string summary, string objectKey = "N1", bool reverted = false) =>
        new(id, origin, DiagramHandEditKind.AddNode, objectKey, summary, DateTime.Now, reverted);

    [Fact]
    public void NoActivityYet_ShowsTheExplicitEmptyMessage_NeverABlankStrip()
    {
        var registry = new FakeDiagramRegistry();
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);

        Assert.Contains("Nog geen activiteit op dit oppervlak.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void JournaledEntry_ForThisSurface_AddsAReadableLine()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "agent-pane", "added node N1 \"Foo\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);

        var texts = _Texts(strip);
        Assert.Contains("added node N1 \"Foo\"", texts);
        Assert.DoesNotContain("Nog geen activiteit op dit oppervlak.", texts);

        window.Close();
    }

    [Fact]
    public void JournaledEntry_ForADifferentSurface_IsNotShown()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-2", DiagramEntry("e1", "agent-pane", "added node N1 \"Foo\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);

        Assert.Contains("Nog geen activiteit op dit oppervlak.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void OperatorOrigin_IsLabelledOperator_RegardlessOfTheCoupledAgent()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "operator", "renamed node A to \"Begin\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        var texts = _Texts(strip);
        Assert.Contains(texts, text => text is not null && text.Contains("operator", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void WhiteboardPlace_ProducesAReadableLine()
    {
        var registry = new FakeWhiteboardRegistry();
        registry.Seed("board-1", new WhiteboardHistoryEntry("e1", "pane-a", WhiteboardHistoryKind.Place, "obj-1", "placed a rectangle reading \"Foo\"", DateTime.Now, Reverted: false));
        var host = new FakeHost(whiteboard: registry);
        var strip = new ActivityStrip(host, "board-1", new WhiteboardActivityJournal(registry), null);
        var window = _Show(strip);

        Assert.Contains("placed a rectangle reading \"Foo\"", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void RevertButton_OnAnUndoneEdit_CallsRevertAndTheRowShowsReverted()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "pane-a", "added node N1 \"Foo\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Terugdraaien"));
        Assert.True(revert.IsEnabled);

        _RaiseClick(revert);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["e1"], registry.RevertCalls);
        Assert.Empty(host.Toasts);
        var reRevert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Terugdraaien"));
        Assert.False(reRevert.IsEnabled);
        Assert.Contains(_Texts(strip), text => text is not null && text.Contains("teruggedraaid", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void RevertButton_OnAnAlreadyRevertedEdit_IsDisabledUpFront()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "pane-a", "added node N1 \"Foo\"", reverted: true));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(registry), null);
        var window = _Show(strip);

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Terugdraaien"));
        Assert.False(revert.IsEnabled);

        window.Close();
    }

    [Fact]
    public void RevertButton_OnAnEraseEntry_IsDisabled_TakingBackARemovedObjectIsNotSupportedYet()
    {
        var registry = new FakeWhiteboardRegistry();
        registry.Seed("board-1", new WhiteboardHistoryEntry("e1", "pane-a", WhiteboardHistoryKind.Erase, "obj-1", "erased an object", DateTime.Now, Reverted: false));
        var host = new FakeHost(whiteboard: registry);
        var strip = new ActivityStrip(host, "board-1", new WhiteboardActivityJournal(registry), null);
        var window = _Show(strip);

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Terugdraaien"));
        Assert.False(revert.IsEnabled);

        window.Close();
    }

    // Avalonia headless has no real pointer pipeline in this test project's setup, so a click is raised directly
    // through the button's routed event rather than simulated input.
    private static void _RaiseClick(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
