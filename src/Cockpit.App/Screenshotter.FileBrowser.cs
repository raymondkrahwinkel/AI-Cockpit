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
    private static Window _FileBrowserScene(int width, int height, bool readOnly)
    {
        var panel = new FileBrowserPanel(() => [new FileBrowserRoute(null, "scene.files", new FileBrowserSceneChannel(readOnly))],
            (_, _) => Task.CompletedTask, _ => { });
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

    private sealed class FileBrowserSceneChannel(bool readOnly) : IPluginUiChannel
    {
        private static readonly DateTimeOffset Day = new(2026, 11, 9, 10, 24, 0, TimeSpan.Zero);

        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) => Task.FromResult(action switch
        {
            FileSourceChannel.Connections => _Answer<IReadOnlyList<FileConnection>>([new("work", "Work notes", "memory", readOnly)]),
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
