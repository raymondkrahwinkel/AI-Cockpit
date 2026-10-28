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

    // The note of the AC-1895 v2 mockup, with its people: Sanne selects a phrase, Joris has his caret after "Linux".
    private const string PresenceNote = "# Weekly sync — 28 October\n\n**Date:** Wednesday 28 October 2026, 14:00 · **Present:** Raymond, Sanne, Joris\n\n## Notes\n- Notes taker: the native editor replaces the webview; the mockups are in review\n- Live bridge spike passed G3: byte-equal on all six fixtures\n- Release 0.141 stays on Friday\n\n## Decisions\n- Jint runs `markdown.ts` in the Depot plugin, not in the host\n\n## Action items\n- [x] Sanne: send the fixtures diff to the team\n- [ ] Joris: measure Jint start-up on Linux\n- [ ] Raymond: review mockups 4, 5 and 6\n";

    private static IReadOnlyList<LivePeer> _PresencePeers(bool away)
    {
        var sanne = PresenceNote.IndexOf("not in the host", StringComparison.Ordinal);
        var joris = PresenceNote.IndexOf("on Linux", StringComparison.Ordinal) + "on Linux".Length;
        return
        [
            new("raymond", "Raymond Krahwinkel", "#3b82f6", "Cockpit", "Editor", "editing", You: true),
            new("sanne", "Sanne de Vries", "#3fb8c4", "web", "Editor", "selecting", Away: away, Caret: sanne + 15, Selection: new LiveRange(sanne, sanne + 15)),
            new("joris", "Joris Bakker", "#d9a441", "Cockpit", "Editor", "editing", Away: away, Caret: joris),
        ];
    }

    private static Window _MarkdownFileScene(int width, int height, string state)
    {
        var presence = state.StartsWith("presence") || state is "viewer" or "schema-mismatch";
        var readOnly = state switch
        {
            "viewer" => new LiveFileStatus(LiveFileState.ReadOnly) { ReadOnlyReason = LiveReadOnlyReason.Viewer },
            "schema-mismatch" => new LiveFileStatus(LiveFileState.ReadOnly, "This note uses a newer Depot editor. You can watch it live; editing comes back once Cockpit's Depot plugin is updated.") { ReadOnlyReason = LiveReadOnlyReason.SchemaMismatch },
            _ => null,
        };
        var text = presence ? PresenceNote : "# md-editor-in-cockpit\n\nCapture an idea straight from the **Depot file browser**, and see *right away* how it looks.\n\n## Wishes\n- A new file in a chosen place\n- A tab or a separate window\n- Saving writes back to Depot with `baseChecksum`\n\n## Tasks\n- [x] Mockup v003\n- [ ] Write the next idea\n";
        var options = new FileWorkspaceOpenOptions(
            presence ? new FileRef("scene", "idea", "Meetings/2026-10-28 Weekly sync.md") : new FileRef("scene", "idea", "Ideas/md-editor-in-cockpit.md"),
            "idea", presence ? "2026-10-28 Weekly sync.md" : "md-editor-in-cockpit.md", "Depot / Work / krahwinkel-it/cockpit",
            Edit: state is not ("readonly" or "preview"), CreateNew: state == "new", InitialText: "# md-editor-in-cockpit\n\n");
        var registry = new WorkspaceTypeRegistry(new ServiceCollection().BuildServiceProvider());
        registry.Register(new WorkspaceTypeRegistration("scene.file", "Markdown", context =>
            new MarkdownFileWorkspace(context, options, new FileSourceChannelClient(new FileSceneChannel(text, state != "readonly", state.StartsWith("conflict"), state is "live" || presence, presence, away: state == "presence-away", status: readOnly ?? _SceneStatus(state))),
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
        if (state == "live")
        {
            body.Editor.EditLine(body.Editor.TextEditor.Document.LineCount - 1, atEnd: true);
        }
        if (presence && readOnly is null)
        {
            body.Editor.EditLine(PresenceNote.Split('\n').ToList().FindIndex(line => line.StartsWith("- Release 0.141", StringComparison.Ordinal)) + 1, atEnd: true);
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

    // Mockup v2 screens 4, 3a and 6: who saved the live note last, as the source reports it (AC-1901).
    private static LiveFileStatus? _SceneStatus(string state)
    {
        var today = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28));
        LiveFileSave? saved = state switch
        {
            "presence-saved" => new LiveFileSave("Sanne", "#3fb8c4", new DateTimeOffset(2026, 10, 28, 14, 12, 0, today)),
            "presence-not-yet" => LiveFileSave.NotYet,
            "presence-agent" => new LiveFileSave("Assistant", "#a78bda", new DateTimeOffset(2026, 10, 28, 14, 15, 0, today)),
            _ => null,
        };
        return saved is null ? null : new LiveFileStatus(LiveFileState.Live) { Saved = saved };
    }

    private sealed class FileSceneChannel(string text, bool writable, bool conflict = false, bool live = false, bool presence = false, bool away = false, LiveFileStatus? status = null) : IPluginUiChannel
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
                new(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), "e7a1b6c9", Encoding.UTF8.GetByteCount(text),
                    presence ? new DateTimeOffset(2026, 10, 28, 14, 12, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28))) : null, "text/markdown")
                    { CanWrite = writable, Live = live, ModifiedBy = presence ? "Sanne" : null }, null), FileSourceChannel.Json),
            FileSourceChannel.LiveOpen when live => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceLiveSnapshot>(
                new("scene", Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), 1, status ?? new(LiveFileState.Live))
                    { Peers = presence ? _PresencePeers(away) : null, OpenUrl = presence ? "https://depot.example/projects/krahwinkel-it/cockpit" : null }, null), FileSourceChannel.Json),
            FileSourceChannel.LivePresenceSet when live => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceLivePresenceSet>(
                JsonSerializer.Deserialize<FileSourceLivePresenceSet>(payload, FileSourceChannel.Json), null), FileSourceChannel.Json),
            FileSourceChannel.LiveClose when live => JsonSerializer.SerializeToElement(new FileSourceAnswer<FileSourceLiveRequest>(new("scene"), null), FileSourceChannel.Json),
            _ => throw new InvalidOperationException("This scene only reads its in-memory source."),
        });
        private bool _MarkRead() => _read = true;

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new FileSceneSubscription();
        private sealed class FileSceneSubscription : IDisposable { public void Dispose() { } }
    }
}
