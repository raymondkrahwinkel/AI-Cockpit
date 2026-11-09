using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cockpit.App.Files;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Files;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.App;

// AC-2236: the host's file browser over an in-memory source, with a folder open and a Markdown file selected.
internal static partial class Screenshotter
{
    private enum HostKeyScene { None, Changed, NotVerified }

    private static Window _FileBrowserScene(int width, int height, bool readOnly, HostKeyScene hostKey = HostKeyScene.None)
    {
        var panel = new FileBrowserPanel(() => [new FileBrowserRoute(null, "scene.files", new FileBrowserSceneChannel(readOnly, hostKey))],
            (_, _) => Task.CompletedTask, _ => { }, () => Task.CompletedTask);
        panel.Ready.GetAwaiter().GetResult();
        return new Window { Width = width, Height = height, Content = panel };
    }

    private static void _OpenFileBrowserFolder(Window window)
    {
        TreeViewItem Row(string path) => window.GetVisualDescendants().OfType<TreeViewItem>()
            .Single(item => item.Tag is FileBrowserPanel.Row row && row.Entry.Path == path);
        Row("Meetings").IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Row("Meetings/2026-11-09 Weekly sync.md").IsSelected = true;
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class FileBrowserSceneChannel(bool readOnly, HostKeyScene hostKey) : IPluginUiChannel
    {
        private static readonly DateTimeOffset Day = new(2026, 11, 9, 10, 24, 0, TimeSpan.Zero);

        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) => Task.FromResult(action switch
        {
            FileSourceChannel.Connections => _Answer<IReadOnlyList<FileConnection>>([new("work", "Work notes", "memory", readOnly) { CanCreateFolder = !readOnly, CanCreateFile = !readOnly }]),
            FileSourceChannel.List when hostKey == HostKeyScene.Changed => _Refused(new FileSourceError(FileSourceErrorKind.HostKey,
                "nas.local presented a different key than the one you trusted. Cockpit did not connect and sent nothing.")
            {
                Trusted = "SHA256:p2QAMXNIC1TJYWeIOttrVc98/R1BUFWu3/LiyKgUfQM",
                Presented = "SHA256:9bW1x2bQk0Q3uVnB2m7nDqJ5yFf0aRk3tLz8sPq4cE0",
            }),
            FileSourceChannel.List when hostKey == HostKeyScene.NotVerified => _Refused(new FileSourceError(FileSourceErrorKind.HostKey,
                "Test this connection in its settings and trust its key first. Nothing is sent until you do.")),
            FileSourceChannel.List => _Answer(new FileListing(payload.Deserialize<FileSourceListRequest>(FileSourceChannel.Json)?.Folder.Path switch
            {
                "/" =>
                [
                    new("Meetings", "Meetings", "Meetings", FileEntryKind.Folder),
                    new("Ideas", "Ideas", "Ideas", FileEntryKind.Folder),
                    new("README.md", "README.md", "README.md", FileEntryKind.File, 2_310, Day.AddDays(-12)),
                    new("roadmap.pdf", "roadmap.pdf", "roadmap.pdf", FileEntryKind.File, 3_481_204, Day.AddDays(-3)),
                ],
                "Meetings" =>
                [
                    new("Meetings/2026-11-09 Weekly sync.md", "2026-11-09 Weekly sync.md", "Meetings/2026-11-09 Weekly sync.md", FileEntryKind.File, 4_812, Day),
                    new("Meetings/2026-11-02 Weekly sync.md", "2026-11-02 Weekly sync.md", "Meetings/2026-11-02 Weekly sync.md", FileEntryKind.File, 3_906, Day.AddDays(-7)),
                    new("Meetings/whiteboard.png", "whiteboard.png", "Meetings/whiteboard.png", FileEntryKind.File, 812_400, Day.AddDays(-7)),
                ],
                _ => [],
            })),
            _ => throw new InvalidOperationException("This scene only lists its in-memory source."),
        });

        private static JsonElement _Refused(FileSourceError error) =>
            JsonSerializer.SerializeToElement(new FileSourceAnswer<FileListing>(null, error), FileSourceChannel.Json);

        private static JsonElement _Answer<T>(T data) where T : class =>
            JsonSerializer.SerializeToElement(new FileSourceAnswer<T>(data, null), FileSourceChannel.Json);

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new SceneSubscription();

        private sealed class SceneSubscription : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
