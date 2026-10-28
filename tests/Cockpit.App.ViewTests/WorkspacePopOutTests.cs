using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Docking;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.Core.Abstractions.Workspaces;
using Cockpit.Core.Layout;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Layout;
using Cockpit.Infrastructure.Workspaces;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.App.Files;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Input;
using System.Text;
using System.Text.Json;

namespace Cockpit.App.ViewTests;

// AC-1751: a workspace popped out into its own window and docked back, as the same body (mockup AC-1749 v003, flow 8).
[Collection("avalonia")]
public sealed class WorkspacePopOutTests
{
    private const string TypeId = "test.markdown";

    private readonly Dictionary<string, IWorkspaceContext> _contexts = new(StringComparer.Ordinal);

    private WorkspacesViewModel _Workspaces(IWorkspaceSettingsStore? store = null)
    {
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(
            new WorkspaceTypeRegistration(TypeId, "Markdown", context =>
            {
                _contexts[context.InstanceKey!] = context;
                return new TextBlock { Text = context.InstanceKey };
            }) { CanFloat = true },
            new NoStorage(),
            NullCockpitSessionObserver.Instance);
        registry.Register(new WorkspaceTypeRegistration("test.docked", "Docked", _ => new TextBlock()), new NoStorage(), NullCockpitSessionObserver.Instance);
        return new WorkspacesViewModel(store, workspaceTypes: registry);
    }

    // Layer 2, view.
    [Fact]
    public Task PopOutAndDockBack_MoveTheSameBody_PinAPreview_AndTheTabLeavesAndComesBackToItsPlace() => HeadlessAvalonia.RunAsync(async () =>
    {
        await _FileWorkspaceFlowAsync();
        var workspaces = _Workspaces();
        var host = new WorkspaceWindowHost(workspaces, _ => Task.CompletedTask, () => null);
        host.Start();
        await workspaces.OpenWorkspaceAsync("test.docked");
        Assert.False(workspaces.CanPopOutActive);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md", Preview: true));
        var popped = workspaces.Active!;
        var slot = workspaces.Tabs.ToList().FindIndex(tab => tab.Id == popped.Id);
        var body = workspaces.ActivePluginBody;
        var pinned = false;
        _contexts["a.md"].Pinned += (_, _) => pinned = true;
        Assert.True(workspaces.CanPopOutActive);

        await workspaces.PopOutAsync(popped.Id);
        Dispatcher.UIThread.RunJobs();

        var window = host.WindowFor(popped.Id);
        Assert.NotNull(window);
        Assert.Contains(body!, window.GetVisualDescendants());
        Assert.True(pinned);
        Assert.DoesNotContain(workspaces.Tabs, tab => tab.Id == popped.Id);
        Assert.NotEqual(popped.Id, workspaces.Active?.Id);

        await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));
        Assert.Single(workspaces.Settings.Workspaces, workspace => workspace.InstanceKey == "a.md");
        Assert.DoesNotContain(workspaces.Tabs, tab => tab.Id == popped.Id);

        await host.DockBackAsync(popped.Id);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(host.WindowFor(popped.Id));
        Assert.Equal(slot, workspaces.Tabs.ToList().FindIndex(tab => tab.Id == popped.Id && tab.IsActive));
        Assert.Same(body, workspaces.ActivePluginBody);
        Assert.False(workspaces.Active!.IsPreview);
    });

    private static async Task _FileWorkspaceFlowAsync()
    {
        var source = new FileSource();
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        using var registration = Cockpit.Infrastructure.Files.FileSourceChannel.Register(hub.For("files"), source);
        var client = new FileSourceChannelClient(new Channel(hub));
        var options = new FileWorkspaceOpenOptions(new FileRef("c1", "doc", "draft.md"), "draft", "draft.md",
            "Depot / Work / Cockpit", Edit: true, CreateNew: true, InitialText: "# Draft\n\n");
        var discard = false;
        var confirmations = 0;
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(new WorkspaceTypeRegistration("test.file", "Markdown", context =>
            new MarkdownFileWorkspace(context, options, client, () =>
            {
                confirmations++;
                return Task.FromResult(discard);
            })) { CanFloat = true }, new NoStorage(), NullCockpitSessionObserver.Instance);
        var workspaces = new WorkspacesViewModel(store: null, workspaceTypes: registry);
        var host = new WorkspaceWindowHost(workspaces, id => workspaces.CloseWorkspaceCommand.ExecuteAsync(id), () => null);
        host.Start();
        try
        {
            await workspaces.OpenWorkspaceAsync("test.file", new WorkspaceOpenOptions("draft", "draft.md"));
            var workspace = workspaces.Active ?? throw new InvalidOperationException("No file workspace opened.");
            var body = workspaces.ActivePluginBody as MarkdownFileWorkspace ?? throw new InvalidOperationException("The host file workspace is missing.");
            await body.Ready;
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length, "typed");
            var document = body.Editor.TextEditor.Document;
            var undo = document.UndoStack;
            Assert.Equal(WorkspaceMarker.Unsaved, workspaces.Tabs.Single(tab => tab.Id == workspace.Id).Marker);
            body.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.S, KeyModifiers = KeyModifiers.Control });
            Assert.Equal("# Draft\n\ntyped", source.Text);
            Assert.Null(workspaces.Tabs.Single(tab => tab.Id == workspace.Id).Marker);
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length, " while floating");
            await workspaces.PopOutAsync(workspace.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(host.WindowFor(workspace.Id));
            await host.DockBackAsync(workspace.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(body, workspaces.ActivePluginBody);
            Assert.Same(document, body.Editor.TextEditor.Document);
            Assert.Same(undo, body.Editor.TextEditor.Document.UndoStack);
            Assert.Equal("# Draft\n\ntyped while floating", body.Editor.Text);
            source.ChangeFromOutside("# someone else");
            await body.SaveAsync();
            Assert.Equal("# someone else", source.Text);
            Assert.Equal("# Draft\n\ntyped while floating", body.Editor.Text);
            await workspaces.CloseWorkspaceCommand.ExecuteAsync(workspace.Id);
            Assert.Contains(workspaces.Settings.Workspaces, entry => entry.Id == workspace.Id);
            await workspaces.PopOutAsync(workspace.Id);
            var floating = host.WindowFor(workspace.Id) ?? throw new InvalidOperationException("The file window is missing.");
            floating.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(host.WindowFor(workspace.Id));
            discard = true;
            await workspaces.CloseWorkspaceCommand.ExecuteAsync(workspace.Id);
            Assert.DoesNotContain(workspaces.Settings.Workspaces, entry => entry.Id == workspace.Id);
            Assert.Equal(3, confirmations);
            source.CanWrite = false;
            options = options with { CreateNew = false, InitialText = "" };
            await workspaces.OpenWorkspaceAsync("test.file", new WorkspaceOpenOptions("viewer", "draft.md"));
            var viewer = workspaces.ActivePluginBody as MarkdownFileWorkspace ?? throw new InvalidOperationException("The viewer workspace is missing.");
            await viewer.Ready;
            Assert.True(viewer.Editor.IsReadOnly);
            viewer.Editor.TextEditor.TextArea.RaiseEvent(new TextInputEventArgs { RoutedEvent = InputElement.TextInputEvent, Text = "not allowed" });
            Assert.Equal("# someone else", viewer.Editor.Text);
        }
        finally
        {
            host.CloseWithTheCockpit();
        }
    }

    private sealed class Channel(PluginChannelHub hub) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            hub.InvokeAsync("files", action, payload, cancellationToken);
        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => hub.Subscribe("files", name, handler);
    }

    private sealed class FileSource : IFileSourceProvider, IFileSource
    {
        public string? Text { get; private set; }
        public bool CanWrite { get; set; } = true;
        private int _version;
        public IReadOnlyList<FileConnection> Connections => [new("c1", "Fake", "memory", false)];
        public FileSourceCapabilities Capabilities => new(CanWrite, FileSourceChannel.MaxContentBytes);
        public event EventHandler? ConnectionsChanged { add { } remove { } }
        public IFileSource Open(string connectionId) => this;
        public Task<FileReadResult> ReadAsync(FileRef file, CancellationToken cancellationToken) => Task.FromResult(
            new FileReadResult(new MemoryStream(Encoding.UTF8.GetBytes(Text ?? throw new FileSourceException(FileSourceErrorKind.NotFound, "Missing file."))),
                $"v{_version}", CanWrite: CanWrite));
        public async Task<FileWriteResult> WriteAsync(FileRef file, Stream content, string? ifVersion, bool mustNotExist, CancellationToken cancellationToken)
        {
            if (!CanWrite) throw new FileSourceException(FileSourceErrorKind.Forbidden, "Read-only file.");
            if (mustNotExist ? Text is not null : ifVersion != $"v{_version}") return new(FileWriteStatus.Conflict, $"v{_version}");
            using var reader = new StreamReader(content, Encoding.UTF8, leaveOpen: true);
            Text = await reader.ReadToEndAsync(cancellationToken);
            return new(FileWriteStatus.Written, $"v{++_version}");
        }
        public void ChangeFromOutside(string text) { Text = text; _version++; }
    }

    // Layer 2, view.
    [Fact]
    public Task AFloatingWorkspace_StaysFloatingWhenTheCockpitCloses_AndReopensInItsWindowAtItsBounds() => HeadlessAvalonia.RunAsync(async () =>
    {
        var config = Path.Combine(Path.GetTempPath(), $"cockpit-ac1751-{Guid.NewGuid():n}.json");
        try
        {
            var store = new WorkspaceSettingsStore(config);
            var bounds = new WindowBoundsStore(config);
            var workspaces = _Workspaces(store);
            var host = new WorkspaceWindowHost(workspaces, _ => Task.CompletedTask, () => bounds);
            host.Start();
            await workspaces.OpenWorkspaceAsync(TypeId, new WorkspaceOpenOptions("a.md", "a.md"));
            var id = workspaces.Active!.Id;
            await bounds.SaveAsync(id, new WindowBounds(120, 80, 700, 500, IsMaximized: false));
            await workspaces.PopOutAsync(id);
            Assert.Equal(new PixelPoint(120, 80), host.WindowFor(id)!.Position);

            var saved = new TaskCompletionSource();
            void OnWritten(string path) => _ = path == config && saved.TrySetResult();
            CockpitConfigFileAccess.Written += OnWritten;
            host.CloseWithTheCockpit();
            await saved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            CockpitConfigFileAccess.Written -= OnWritten;

            var restarted = _Workspaces(store);
            await restarted.InitializeAsync();
            var restartedHost = new WorkspaceWindowHost(restarted, _ => Task.CompletedTask, () => bounds);
            restartedHost.Start();

            Assert.DoesNotContain(restarted.Tabs, tab => tab.Id == id);
            var reopened = restartedHost.WindowFor(id);
            Assert.NotNull(reopened);
            Assert.Equal(new PixelPoint(120, 80), reopened.Position);
            Assert.Equal((700d, 500d), (reopened.Width, reopened.Height));
            restartedHost.CloseWithTheCockpit();
        }
        finally
        {
            File.Delete(config);
        }
    });

    private sealed class NoStorage : IPluginStorage
    {
        public T? Get<T>(string key) => default;

        public void Set<T>(string key, T value)
        {
        }
    }
}
