using System.Text.Json;
using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;

namespace Cockpit.Plugin.Depot.UI;

internal static class DepotMarkdownFiles
{
    public const string WorkspaceTypeId = "depot.markdown";
    public static bool IsMarkdown(DepotBrowserNode node) => node.Kind == DepotBrowserKind.Document
        && (node.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || node.Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    // A live note: the backend starts the note's session first, and the editor tab it opens takes it over. The key is the
    // note's own, so a second Join brings the open tab to the front and starts no second session; a tab that fails to open lets it go.
    public static async Task OpenNoteAsync(ICockpitUiHost host, DepotConnectionRegistration connection, string project, string path, bool edit)
    {
        var address = project.Split('/', 2);
        var payload = JsonSerializer.SerializeToElement(
            new DepotNoteLiveRequest(connection.Id, address[0], address.ElementAtOrDefault(1) ?? string.Empty, path), DepotChannel.Json);
        var key = InstanceKey(connection, project, path);
        var open = host.GetOpenWorkspaces(WorkspaceTypeId).Any(instance => instance.InstanceKey == key);
        if (!open)
        {
            await host.Channel.InvokeAsync(DepotNoteLiveChannel.Open, payload).ConfigureAwait(true);
        }

        try
        {
            await OpenAsync(host, connection, project, path, edit).ConfigureAwait(true);
        }
        catch when (!open)
        {
            await host.Channel.InvokeAsync(DepotNoteLiveChannel.Close, payload).ConfigureAwait(true);
            throw;
        }
    }

    private static string InstanceKey(DepotConnectionRegistration connection, string project, string path) => $"depot:{connection.Id}:{project}:{path}";

    public static Task OpenAsync(ICockpitUiHost host, DepotConnectionRegistration connection,
        string project, string path, bool edit, bool createNew = false, bool preview = false) =>
        host.OpenFileWorkspaceAsync(WorkspaceTypeId, new FileWorkspaceOpenOptions(
            DepotFileRefs.Create(connection.Id, project, path), InstanceKey(connection, project, path),
            Path.GetFileName(path), $"Depot / {connection.Name} / {project}", edit, createNew,
            createNew ? $"# {Path.GetFileNameWithoutExtension(path)}\n\n" : "", preview));
}
