using System.Text.Json;
using Avalonia.Controls;
using Cockpit.App.Plugins;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.App.Views;
using Cockpit.Core.Abstractions.Remote;
using Cockpit.Infrastructure.BackendApi;
using Cockpit.Infrastructure.Plugins;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Material.Icons;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cockpit.App.Composition;

internal static class RemoteFileUi
{
    internal static IDisposable Attach(CockpitUiHost host, IRemoteServer server)
    {
        var files = RemoteProjectFiles.For(server);
        var hub = new PluginChannelHub(NullLogger<PluginChannelHub>.Instance);
        var registration = files.Register(hub.For("server-project-files"));
        var channel = new ProjectChannel(files, new CockpitUiHost.UiChannel(hub, "server-project-files"));
        host.AddRemoteFileWorkspaceType("server-project-files", "Files", server.Name, channel);
        host.AddFileBrowser(new FileBrowserRegistration("server-project-files", "Files", MaterialIconKind.FolderOutline, "server-project-files"));
        return registration;
    }

    internal static async Task ShowWorktreesAsync(WorktreesViewModel worktrees, Window owner, SurfaceWindows surfaces)
    {
        if (surfaces.TryActivateAsync(typeof(WorktreesDialog)) is { } pending)
        {
            await pending;
            return;
        }
        await worktrees.RefreshAsync();
        await surfaces.ShowAsync(typeof(WorktreesDialog), new WorktreesDialog { DataContext = worktrees }, owner);
    }

    private sealed class ProjectChannel(RemoteProjectFiles files, IPluginUiChannel inner) : IPluginUiChannel
    {
        public async Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default)
        {
            if (action == Cockpit.Plugins.Abstractions.Files.FileSourceChannel.Connections)
            {
                await files.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            return await inner.InvokeAsync(action, payload, cancellationToken).ConfigureAwait(false);
        }

        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => inner.Subscribe(name, handler);
    }
}
