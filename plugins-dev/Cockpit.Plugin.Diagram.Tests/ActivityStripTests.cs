using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.Core.Abstractions.Diagrams;
using Cockpit.Core.Abstractions.Whiteboard;
using Cockpit.Core.Abstractions.Wireframe;
using Cockpit.Plugin.Diagram.Collab;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.CompanionTools;
using Cockpit.Plugins.Abstractions.Consent;
using Cockpit.Plugins.Abstractions.Docking;
using Cockpit.Plugins.Abstractions.ManagedCli;
using Cockpit.Plugins.Abstractions.Mcp;
using Cockpit.Plugins.Abstractions.Notifications;
using Cockpit.Plugins.Abstractions.Profiles;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.Widgets;
using Cockpit.Plugins.Abstractions.Workspaces;

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

        public List<string> RevertCalls { get; } = [];

        public string? NextRevertRefusal { get; set; }

        public event Action<string, string>? TextChanged { add { } remove { } }

        // PresenceIndicators (AC-847) needs a real CouplingChanged, unlike ActivityStrip which never subscribes to
        // it — a no-op stand-in here would mean its tests could never see a coupling appear or drop.
        public event Action<DiagramCouplingChange>? CouplingChanged;

        public event Action<string, DiagramProposal?>? ProposalChanged { add { } remove { } }

        public event Action<string, string>? ObjectEdited { add { } remove { } }

        public event Action<string>? HistoryChanged;

        public void Seed(string surfaceId, DiagramHistoryEntry entry) =>
            (_history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = []).Add(entry);

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

        public DiagramFidelity? CheckFidelity(string source) => new([]);

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

        public (string? Text, string Summary, string? Refusal) ComputeHandEdit(string source, DiagramHandEdit edit) => (source, "", null);

        public DiagramEditSupport EditSupport(string surfaceId) => new(DiagramEditDialect.Flowchart, null);

        public IReadOnlyList<DiagramErAttribute> EntityAttributes(string surfaceId, string entity) => [];

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
    }

    internal sealed class FakeWhiteboardRegistry : IWhiteboardAccessRegistry
    {
        private readonly Dictionary<string, List<WhiteboardHistoryEntry>> _history = new();

        public List<string> RevertCalls { get; } = [];

        public event Action<string, byte[]>? SnapshotChanged { add { } remove { } }

        // Same reason as FakeDiagramRegistry's CouplingChanged above: PresenceIndicators actually subscribes.
        public event Action<WhiteboardCouplingChange>? CouplingChanged;

        public event Action<string, string, WhiteboardPlacement>? ObjectPlaced { add { } remove { } }

        public event Action<string, string>? ObjectErased { add { } remove { } }

        public event Action<string>? HistoryChanged;

        public void Seed(string surfaceId, WhiteboardHistoryEntry entry) =>
            (_history.TryGetValue(surfaceId, out var list) ? list : _history[surfaceId] = []).Add(entry);

        public void Raise(string surfaceId) => HistoryChanged?.Invoke(surfaceId);

        public void SetCoupling(string surfaceId, WhiteboardCoupling? coupling) =>
            CouplingChanged?.Invoke(new WhiteboardCouplingChange(surfaceId, coupling));

        public IReadOnlyList<WhiteboardHistoryEntry> History(string surfaceId) =>
            _history.TryGetValue(surfaceId, out var list) ? list : [];

        public string? Revert(string surfaceId, string entryId)
        {
            RevertCalls.Add(entryId);
            return "Restoring a deleted object cannot be reverted yet.";
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
    }

    // F2.13/AC-1401: ActivityStrip and the workspace bodies take ICockpitUiHost now the UI part is its own assembly.
    // Unlike ICockpitHost this interface has no default members, so everything beyond what a test actually reaches
    // (ShowToast, and the members DiagramWorkspaceBody/WireframeWorkspaceBody/SurfaceSessionBinding touch) throws —
    // same shape as Cockpit.Plugin.Discord.Tests.FakeCockpitUiHost. The registry reaches production code through
    // UiChannel (TestChannel), never through a Services lookup — ICockpitUiHost carries no Services at all.
    internal sealed class FakeHost : ICockpitUiHost
    {
        // AC-904 hands in a real IWireframeAccessRegistry rather than a fake: the wireframe surface's own tests want
        // the line surgery that actually runs, not a stand-in that agrees with them.
        public FakeHost(
            FakeDiagramRegistry? diagram = null,
            FakeWhiteboardRegistry? whiteboard = null,
            IWireframeAccessRegistry? wireframe = null)
        {
            UiChannel = diagram is null && whiteboard is null && wireframe is null ? null : TestChannel.For(diagram, whiteboard, wireframe);
        }

        // AC-1400: what a window reaches the registries through; null (no registry at all) is the "older host" state.
        public IPluginUiChannel? UiChannel { get; }

        public List<string> Toasts { get; } = [];

        public IPluginUiChannel Channel => throw new NotSupportedException("No test reaches host.Channel directly — production code takes its own channel parameter.");

        public IPluginStorage Storage => throw new NotSupportedException();

        public bool HasSettings => throw new NotSupportedException();

        public string? ActivePaneId => null;

        public string? ActiveSessionWorkingDirectory => null;

        public SessionUsageSnapshot? ActiveSessionUsage => null;

        public event EventHandler? ActiveSessionChanged { add { } remove { } }

        public void ShowToast(string message, PluginToastSeverity severity = PluginToastSeverity.Information, string? actionLabel = null, Action? onAction = null) =>
            Toasts.Add(message);

        public void AddSettings(Func<Control> createView)
        {
        }

        public void AddSettings(Func<Control> createView, string category)
        {
        }

        public Task ShowSettingsAsync() => Task.CompletedTask;

        public void OnSettingsSaved(Action callback)
        {
        }

        public void AddSideMenuButton(string title, Action onInvoke)
        {
        }

        public SideMenuButtonBadge AddSideMenuButtonWithBadge(string title, Action onInvoke) =>
            throw new NotSupportedException();

        public void AddSideMenuSection(string title, Func<Control> createView)
        {
        }

        public void AddSessionHeaderItem(Func<IPluginSessionContext, Control> createView)
        {
        }

        public void AddSessionBanner(Func<IPluginSessionContext, Control> createView)
        {
        }

        public void AddSessionHeaderAction(PluginSessionAction action)
        {
        }

        public void AddToolbarAction(ToolbarAction action)
        {
        }

        public void AddShortcut(PluginShortcut shortcut)
        {
        }

        public void AddConversationPicker(ConversationPickerRegistration picker)
        {
        }

        public void AddProviderConfigView(string providerId, Func<string?, IPluginProviderConfigView> createView)
        {
        }

        public void AddWidget(WidgetRegistration registration)
        {
        }

        public void AddDockPanel(DockPanelRegistration registration)
        {
        }

        public void AddCompanionTool(CompanionToolRegistration registration)
        {
        }

        public void AddWorkspaceType(WorkspaceTypeRegistration registration)
        {
        }

        public Task OpenWorkspaceAsync(string workspaceTypeId) => Task.CompletedTask;

        public Control? CreateEmbeddedSessionView(string paneId) => null;

        public Task ShowDialogAsync(string title, Func<Control> createContent, double width = 720, double height = 560) =>
            Task.CompletedTask;

        public Task ShowDialogAsync(string title, Func<Control> createContent, string singleInstanceKey, double width = 720, double height = 560) =>
            Task.CompletedTask;

        public Task ShowNewSessionDialogAsync(NewSessionPrefill? prefill = null, Action<string>? onStarted = null, Action? onCancelled = null) =>
            Task.CompletedTask;

        public Control CreateMarkdownView(string markdown) => new TextBlock();

        public Control CreateHelpHint(string article, string? section = null, string? label = null) => new Panel();

        public void OpenHelp(string article, string? section = null)
        {
        }

        public bool HasHelp(string article, string? section = null) => false;

        public Task SetClipboardTextAsync(string text) => Task.CompletedTask;

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel = "Confirm") => Task.FromResult(false);

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request) =>
            throw new NotSupportedException("No test reaches consent.");

        public Task<ConsentDecision> RequestConsentAsync(ConsentRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("No test reaches consent.");

        public Task<IReadOnlyList<PluginProfileInfo>> GetProfilesAsync() => Task.FromResult<IReadOnlyList<PluginProfileInfo>>([]);

        public Task SendToSessionAsync(string paneId, string text) => Task.CompletedTask;

        public Task InsertIntoSessionAsync(string paneId, string text) => Task.CompletedTask;

        public Task<IReadOnlyList<ProjectMemoryRow>> GetProjectMemoryRowsAsync(string? paneId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectMemoryRow>>([]);

        public Task<IReadOnlyDictionary<string, string>?> SendIntent(string targetPluginId, string action, IReadOnlyDictionary<string, string> data) =>
            Task.FromResult<IReadOnlyDictionary<string, string>?>(null);

        public bool CanSendIntent(string targetPluginId, string action) => false;

        public string? ResolveManagedCliPath(string cliName) => null;

        public Task<ManagedCliStatus> GetManagedCliStatusAsync(string cliName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ManagedCliInstallResult> InstallManagedCliAsync(string cliName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public bool RemoveManagedCli(string cliName) => throw new NotSupportedException();

        public Task<bool> GetManagedCliAutoUpdateAsync(string cliName, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task SetManagedCliAutoUpdateAsync(string cliName, bool enabled, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<PluginMcpAuthState> GetMcpServerAuthStateAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PluginMcpSignInOutcome> SignInMcpServerAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
        var window = _Show(strip);

        Assert.Contains("No activity on this surface yet.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void JournaledEntry_ForThisSurface_AddsAReadableLine()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "agent-pane", "added node N1 \"Foo\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
        var window = _Show(strip);

        var texts = _Texts(strip);
        Assert.Contains("added node N1 \"Foo\"", texts);
        Assert.DoesNotContain("No activity on this surface yet.", texts);

        window.Close();
    }

    [Fact]
    public void JournaledEntry_ForADifferentSurface_IsNotShown()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-2", DiagramEntry("e1", "agent-pane", "added node N1 \"Foo\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
        var window = _Show(strip);

        Assert.Contains("No activity on this surface yet.", _Texts(strip));

        window.Close();
    }

    [Fact]
    public void OperatorOrigin_IsLabelledOperator_RegardlessOfTheCoupledAgent()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "operator", "renamed node A to \"Begin\""));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
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
        var strip = new ActivityStrip(host, "board-1", new WhiteboardActivityJournal(TestChannel.Whiteboard(registry)), null);
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
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
        var window = _Show(strip);
        strip.SetSession("pane-a", "Werksessie");

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Revert"));
        Assert.True(revert.IsEnabled);

        _RaiseClick(revert);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(["e1"], registry.RevertCalls);
        Assert.Empty(host.Toasts);
        var reRevert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Revert"));
        Assert.False(reRevert.IsEnabled);
        Assert.Contains(_Texts(strip), text => text is not null && text.Contains("reverted", StringComparison.Ordinal));

        window.Close();
    }

    [Fact]
    public void RevertButton_OnAnAlreadyRevertedEdit_IsDisabledUpFront()
    {
        var registry = new FakeDiagramRegistry();
        registry.Seed("surface-1", DiagramEntry("e1", "pane-a", "added node N1 \"Foo\"", reverted: true));
        var host = new FakeHost(registry);
        var strip = new ActivityStrip(host, "surface-1", new DiagramActivityJournal(TestChannel.Diagram(registry)), null);
        var window = _Show(strip);

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Revert"));
        Assert.False(revert.IsEnabled);

        window.Close();
    }

    [Fact]
    public void RevertButton_OnAnEraseEntry_IsDisabled_TakingBackARemovedObjectIsNotSupportedYet()
    {
        var registry = new FakeWhiteboardRegistry();
        registry.Seed("board-1", new WhiteboardHistoryEntry("e1", "pane-a", WhiteboardHistoryKind.Erase, "obj-1", "erased an object", DateTime.Now, Reverted: false));
        var host = new FakeHost(whiteboard: registry);
        var strip = new ActivityStrip(host, "board-1", new WhiteboardActivityJournal(TestChannel.Whiteboard(registry)), null);
        var window = _Show(strip);

        var revert = strip.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "Revert"));
        Assert.False(revert.IsEnabled);

        window.Close();
    }

    // Avalonia headless has no real pointer pipeline in this test project's setup, so a click is raised directly
    // through the button's routed event rather than simulated input.
    private static void _RaiseClick(Button button) =>
        button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
