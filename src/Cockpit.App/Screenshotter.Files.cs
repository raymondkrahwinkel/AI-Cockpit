using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Cockpit.App.Files;
using Cockpit.App.Plugins;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App;

internal static partial class Screenshotter
{
    private static Window _NewMarkdownFileDialogScene()
    {
        var window = PluginScenes.Build("plugin:depot-new-markdown", 480, 480);
        Controls.CockpitWindowChrome.Apply(window, "New Markdown file");
        return window;
    }

    private static Window _MarkdownFileScene(int width, int height, string state)
    {
        const string text = "# md-editor-in-cockpit\n\nCapture an idea straight from the **Depot file browser**, and see *right away* how it looks.\n\n## Wishes\n- A new file in a chosen place\n- A tab or a separate window\n- Saving writes back to Depot with `baseChecksum`\n\n## Tasks\n- [x] Mockup v003\n- [ ] Write the next idea\n";
        var options = new FileWorkspaceOpenOptions(new FileRef("scene", "idea", "Ideas/md-editor-in-cockpit.md"),
            "idea", "md-editor-in-cockpit.md", "Depot / Work / krahwinkel-it/cockpit",
            Edit: state is not ("readonly" or "preview"), CreateNew: state == "new", InitialText: "# md-editor-in-cockpit\n\n");
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(new WorkspaceTypeRegistration("scene.file", "Markdown", context =>
            new MarkdownFileWorkspace(context, options, new FileSourceChannelClient(new FileSceneChannel(text, state != "readonly", state.StartsWith("conflict"))),
                () => Task.FromResult(false))) { CanFloat = true }, new PluginScenes.SceneStorage(), NullCockpitSessionObserver.Instance);
        var cockpit = new CockpitViewModel(workspaceTypeRegistry: registry);
        var workspaces = cockpit.Workspaces;
        if (state == "preview")
        {
            workspaces.OpenWorkspaceAsync("scene.file", new("notes", "Notes.md")).GetAwaiter().GetResult();
        }
        workspaces.OpenWorkspaceAsync("scene.file", new("idea", options.Title, state == "preview")).GetAwaiter().GetResult();
        var entry = workspaces.Active ?? throw new InvalidOperationException("The file scene did not open.");
        var body = workspaces.BodyFor(entry) as MarkdownFileWorkspace ?? throw new InvalidOperationException("The editor scene is missing.");
        body.Ready.GetAwaiter().GetResult();
        if (state.StartsWith("conflict"))
        {
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length, "- Reply when it is done\n");
            body.Editor.TextEditor.Document.Replace(body.Editor.Text.IndexOf("- [ ] Write", StringComparison.Ordinal), 5, "- [x]");
            body.SaveAsync().GetAwaiter().GetResult();
            if (state == "conflict-compare")
            {
                body.ToggleCompare();
            }
        }
        if (state is "editing" or "floating")
        {
            body.Editor.TextEditor.Document.Insert(body.Editor.Text.Length, "\nA new idea, ready to save.");
        }
        if (state == "floating")
        {
            workspaces.PopOutAsync(entry.Id).GetAwaiter().GetResult();
            return new Docking.DockPanelWindow(entry.Id, entry.Name, body, boundsStore: null, "Put this workspace back in the tab strip")
                { Width = width, Height = height };
        }
        return new MainWindow { Width = width, Height = height, DataContext = cockpit };
    }

    private sealed class FileSceneChannel(string text, bool writable, bool conflict = false) : IPluginUiChannel
    {
        private string Changed => text.Replace("`baseChecksum`" + (char)10, "`baseChecksum`" + (char)10 + "- Compare before you overwrite" + (char)10);
        private bool _read;

        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) => Task.FromResult(action switch
        {
            FileSourceChannel.Write => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileWriteResult>(new(FileWriteStatus.Conflict, "f00dcafe"), null), FileSourceChannel.Json),
            FileSourceChannel.Read when conflict && _read => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceReadAnswer>(
                new(Convert.ToBase64String(Encoding.UTF8.GetBytes(Changed)), "f00dcafe", Encoding.UTF8.GetByteCount(Changed), new DateTimeOffset(2026, 10, 28, 15, 2, 0, TimeSpan.Zero), "text/markdown")
                    { CanWrite = writable, ModifiedBy = "cockpit-assistant" }, null), FileSourceChannel.Json),
            FileSourceChannel.Connections => JsonSerializer.SerializeToElement(new FileSourceAnswer<IReadOnlyList<FileConnection>>([new("scene", "Work", "memory", !writable)], null), FileSourceChannel.Json),
            FileSourceChannel.Read when _MarkRead() => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceReadAnswer>(
                new(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "e7a1b6c9", Encoding.UTF8.GetByteCount(text), null, "text/markdown") { CanWrite = writable }, null), FileSourceChannel.Json),
            _ => throw new InvalidOperationException("This scene only reads its in-memory source."),
        });
        private bool _MarkRead() => _read = true;

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new FileSceneSubscription();
        private sealed class FileSceneSubscription : IDisposable { public void Dispose() { } }
    }
}
