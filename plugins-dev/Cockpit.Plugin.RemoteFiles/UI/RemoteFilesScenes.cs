using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Cockpit.Plugin.RemoteFiles.Contracts;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Plugin.RemoteFiles.UI;

// Render-only states over an in-memory channel; the real checks run in the backend.
internal static class RemoteFilesScenes
{
    private static readonly RemotePin Pinned = new("ssh-ed25519 (256-bit)", "SHA256:p2QAMXNIC1TJYWeIOttrVc98/R1BUFWu3/LiyKgUfQM", new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero));
    private static readonly RemoteConnection Nas = new("nas", "NAS", RemoteFilesChannel.Sftp, "nas.home.lan", 22, "raymond", UseKey: true, Pin: Pinned);
    private static readonly RemoteConnection Web = new("web", "Website", RemoteFilesChannel.Ftps, "ftp.example.com", 21, "deploy", ReadOnly: false);
    private static readonly RemoteConnection Printer = new("printer", "Label printer", RemoteFilesChannel.Ftp, "192.168.1.40", 21, "admin");
    private static readonly RemotePresented Presented = new("ssh-ed25519 (256-bit)", "SHA256:9bW1x2bQk0Q3uVnB2m7nDqJ5yFf0aRk3tLz8sPq4cE0");

    public static IReadOnlyDictionary<string, Func<Control>> All { get; } = new Dictionary<string, Func<Control>>(StringComparer.Ordinal)
    {
        ["remote-files-settings-webdav"] = () => _View(Environment.MachineName, [new RemoteConnection("dav", "Nextcloud", RemoteFilesChannel.WebDav, "", 443, "user", DavUrl: "https://cloud.example.com/remote.php/dav/files/user/")], "dav"),
        ["remote-files-settings-webdav-http"] = () => _View(Environment.MachineName, [new RemoteConnection("dav", "Local DAV", RemoteFilesChannel.WebDav, "", 443, "user", DavUrl: "http://localhost:18788/dav/", UseBearer: true)], "dav"),
        ["remote-files-settings-empty"] = () => _View(Environment.MachineName, [], null),
        ["remote-files-settings"] = () => _View(Environment.MachineName, [Nas, Web, Printer], "nas"),
        ["remote-files-settings-new"] = () => _View(Environment.MachineName, [new RemoteConnection("new", "Backup box", RemoteFilesChannel.Sftp, "backup.home.lan", 22, "raymond")], "new"),
        ["remote-files-settings-ftp"] = () => _View(Environment.MachineName, [Nas, Printer], "printer"),
        ["remote-files-settings-server"] = () => _View("huis-cockpit", [Nas with { UseKey = false }], "nas"),
        ["remote-files-trust"] = () => RemoteDialogs.Trust(Nas with { Pin = null }, Presented, () => { }),
        ["remote-files-trust-certificate"] = () => RemoteDialogs.Trust(Web, new RemotePresented("certificate",
            "4F:1A:9C:22:B0:7E:51:D3:88:0C:3A:6B:F2:19:E4:7D:55:A0:2C:91:6E:3F:B8:47:D2:0A:63:C9:1E:85:F4:2B",
            "CN=ftp.example.com", "CN=ftp.example.com", new DateTimeOffset(2027, 8, 14, 0, 0, 0, TimeSpan.Zero)), () => { }),
        ["remote-files-changed"] = () => RemoteDialogs.Changed(Nas, Presented),
    };

    private static Control _View(string machine, RemoteConnection[] connections, string? open)
    {
        var view = new RemoteFilesSettingsControl(new SceneChannel(machine, connections), (_, _) => Task.CompletedTask);
        while (!view.Ready.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
        }
        view.Expand(open);
        return new ScrollViewer { Content = view, Padding = new Avalonia.Thickness(24, 16) };
    }

    private sealed class SceneChannel(string machine, RemoteConnection[] connections) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(action == RemoteFilesChannel.Connections
                ? JsonSerializer.SerializeToElement(new RemoteConnectionsAnswer(connections,
                    connections.ToDictionary(c => c.Id, c => new RemoteSecretsSet(c.Id != "new", c.UseKey, false)), machine), RemoteFilesChannel.Json)
                : throw new InvalidOperationException("This scene only answers the settings view's read."));

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
