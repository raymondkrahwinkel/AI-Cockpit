using Avalonia;
using Avalonia.Controls;
using System.Text.Json;
using Cockpit.Plugins.Abstractions.Channels;
using Cockpit.App.Files;
using Cockpit.App.Services;
using Cockpit.App.ViewModels;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;
using Cockpit.Plugins.Abstractions.Workspaces;
using Material.Icons;
using Microsoft.Extensions.DependencyInjection;

namespace Cockpit.App.Plugins;

internal sealed partial class CockpitUiHost
{
    private readonly HashSet<string> _fileWorkspaceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _localFileWorkspaceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IPluginUiChannel> _fileWorkspaceChannels = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, string Key), FileWorkspaceOpenOptions> _pendingFiles = [];

    public void AddFileWorkspaceType(string workspaceTypeId, string title)
    {
        _RegisterFileWorkspace(workspaceTypeId, title, Channel);
        _localFileWorkspaceTypes[workspaceTypeId] = title;
    }

    internal void RegisterRemoteFileWorkspaces(string server, IPluginUiChannel channel)
    {
        foreach (var (id, title) in _localFileWorkspaceTypes)
            AddRemoteFileWorkspaceType(id, title, server, channel);
    }

    internal void AddRemoteFileWorkspaceType(string workspaceTypeId, string title, string server,
        IPluginUiChannel channel) =>
        _RegisterFileWorkspace(_RemoteFileType(workspaceTypeId, server), title, channel);

    private static string _RemoteFileType(string id, string server) => $"{id}@server:{Uri.EscapeDataString(server)}";

    private void _RegisterFileWorkspace(string id, string title, IPluginUiChannel channel)
    {
        _fileWorkspaceChannels[id] = channel;
        if (!_fileWorkspaceTypes.Add(id)) return;
        var registration = new WorkspaceTypeRegistration(id, title, context =>
        {
            var options = context.InstanceKey is { } key ? _pendingFiles.GetValueOrDefault((id, key)) : null;
            options ??= context.Storage.Get<FileWorkspaceOpenOptions>("file");
            if (options?.File is null || string.IsNullOrWhiteSpace(options.File.Path))
                return new TextBlock { Text = "Open a Markdown file from a file browser.", Margin = new Thickness(24) };
            options = options with { Location = options.Location ?? "", InitialText = options.InitialText ?? "" };
            context.Storage.Set("file", options with { Edit = false, CreateNew = false, InitialText = "" });
            return new MarkdownFileWorkspace(context, options, new FileSourceChannelClient(new FileWorkspaceChannel(() => _fileWorkspaceChannels[id])), () =>
                ConfirmAsync("Discard unsaved changes?", $"Close {options.Title} without saving? Your changes will be lost.", "Discard changes"));
        }) { CanFloat = true, IconKind = MaterialIconKind.FileDocumentOutline };
        if (!services.GetRequiredService<IWorkspaceTypeRegistry>().Register(registration, Storage, host.Sessions))
            _fileWorkspaceTypes.Remove(id);
    }

    public Task OpenFileWorkspaceAsync(string workspaceTypeId, FileWorkspaceOpenOptions options) =>
        _OpenFileWorkspaceAsync(workspaceTypeId, options);

    internal Task OpenRemoteFileWorkspaceAsync(string workspaceTypeId, string server, FileWorkspaceOpenOptions options) =>
        _OpenFileWorkspaceAsync(_RemoteFileType(workspaceTypeId, server), options with
        {
            Location = options.Location.Length == 0 ? server : $"{server} / {options.Location}",
        });

    private async Task _OpenFileWorkspaceAsync(string typeId, FileWorkspaceOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.File);
        if (!_fileWorkspaceTypes.Contains(typeId))
            throw new InvalidOperationException("Register the file workspace type before opening a file.");
        if (string.IsNullOrWhiteSpace(options.InstanceKey) || string.IsNullOrWhiteSpace(options.File.Path))
            throw new ArgumentException("The file workspace needs a key and a file path.", nameof(options));
        if (services.GetService<CockpitViewModel>()?.Workspaces is not { } workspaces) return;
        await UiThreadCall.DispatchAsync(async () =>
        {
            var key = (typeId, options.InstanceKey);
            _pendingFiles[key] = options;
            try
            {
                await workspaces.OpenWorkspaceAsync(typeId, new WorkspaceOpenOptions(options.InstanceKey, options.Title)).ConfigureAwait(true);
                var entry = workspaces.Settings.Workspaces.FirstOrDefault(w => w.Type.Id == typeId && w.InstanceKey == options.InstanceKey);
                if (entry is not null && workspaces.BodyFor(entry) is MarkdownFileWorkspace body)
                {
                    await body.Ready.ConfigureAwait(true);
                    if (options.Edit || options.CreateNew) body.BeginEdit();
                }
            }
            finally { _pendingFiles.Remove(key); }
        }).ConfigureAwait(true);
    }

    private sealed class FileWorkspaceChannel(Func<IPluginUiChannel> channel) : IPluginUiChannel
    {
        public Task<JsonElement> InvokeAsync(string action, JsonElement payload, CancellationToken cancellationToken = default) =>
            channel().InvokeAsync(action, payload, cancellationToken);
        public IDisposable Subscribe(string name, Action<PluginChannelEvent> handler) => channel().Subscribe(name, handler);
    }
}
