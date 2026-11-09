using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugin.LocalFiles.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.LocalFiles.UI;

// Render-only states over an in-memory channel; the real checks run in the backend.
internal static class LocalFilesScenes
{
    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>(StringComparer.Ordinal)
    {
        ["local-files-settings-empty"] = () => _View(new SceneChannel(Environment.MachineName, []), null),
        ["local-files-settings"] = () => _View(new SceneChannel(Environment.MachineName,
        [
            new LocalRoot("a", @"C:\Users\raymond\Documents", Writable: false),
            new LocalRoot("b", @"C:\Source\cockpit", Writable: true, Project: true),
        ]), @"C:\Users\raymond\AppData\Roaming\Cockpit"),
        ["local-files-settings-server"] = () => _View(new SceneChannel("huis-cockpit",
        [
            new LocalRoot("a", "/srv/projects", Writable: false),
            new LocalRoot("b", "/var/lib", Writable: false),
        ], ("b", "/var/lib/cockpit")), "/var/lib/cockpit"),
        ["local-files-server-picker"] = () => _Picker(),
    };

    private static Control _Picker()
    {
        var picker = new ServerFolderPicker(new SceneChannel("huis-cockpit", []), _ => { });
        picker.Ready.GetAwaiter().GetResult();
        return picker;
    }

    private static Control _View(SceneChannel channel, string? refused)
    {
        var projects = channel.Machine == Environment.MachineName
            ? new[] { new PluginProjectInfo("cockpit", "cockpit") { SourceDirectory = @"C:\Source\cockpit" }, new PluginProjectInfo("depot", "depot") { SourceDirectory = @"C:\Source\depot" } }
            : [new PluginProjectInfo("homeautomation", "homeautomation") { SourceDirectory = "/srv/projects/homeautomation" }];
        var view = new LocalFilesSettingsControl(channel, () => Task.FromResult<IReadOnlyList<PluginProjectInfo>>(projects), (_, _) => Task.CompletedTask);
        view.Ready.GetAwaiter().GetResult();
        if (refused is not null)
        {
            channel.Refuse = refused;
            // Safe only because SceneChannel answers synchronously; a real channel would need awaiting.
            _ = view.AddAsync(refused, project: false);
            Dispatcher.UIThread.RunJobs();
        }
        return new ScrollViewer { Content = view, Padding = new Avalonia.Thickness(24, 16) };
    }

    private sealed class SceneChannel(string machine, LocalRoot[] roots, (string Id, string Path)? hidden = null) : IPluginUiChannel
    {
        public string Machine => machine;

        public string? Refuse { get; set; }

        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(JsonSerializer.SerializeToElement<object>(action switch
            {
                LocalFilesChannel.Roots => new LocalRootsAnswer(roots,
                    hidden is { } inside ? new Dictionary<string, IReadOnlyList<string>> { [inside.Id] = [inside.Path] } : [], machine),
                LocalFilesChannel.Check => new LocalRootCheck(null, [],
                    $"{Refuse} is Cockpit's own state folder, or lies inside it. It holds credentials, so it cannot be added."),
                LocalFilesChannel.Folders => new LocalFolders("/var/lib", ["/var/lib/apt", "/var/lib/cockpit", "/var/lib/docker", "/var/lib/postgresql"], null),
                _ => throw new InvalidOperationException("This scene only answers the settings view's reads."),
            }, LocalFilesChannel.Json));

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
