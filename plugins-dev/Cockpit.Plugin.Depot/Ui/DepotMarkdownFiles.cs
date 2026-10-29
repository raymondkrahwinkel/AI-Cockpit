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
    // note's own, so a second Join brings the tab that is open to the front.
    public static async Task OpenNoteAsync(ICockpitUiHost host, DepotConnectionRegistration connection, string project, string path, bool edit)
    {
        var address = project.Split('/', 2);
        await host.Channel.InvokeAsync(DepotNoteLiveChannel.Open, JsonSerializer.SerializeToElement(
            new DepotNoteLiveRequest(connection.Id, address[0], address.ElementAtOrDefault(1) ?? string.Empty, path), DepotChannel.Json)).ConfigureAwait(true);
        await OpenAsync(host, connection, project, path, edit).ConfigureAwait(true);
    }

    public static Task OpenAsync(ICockpitUiHost host, DepotConnectionRegistration connection,
        string project, string path, bool edit, bool createNew = false, bool preview = false) =>
        host.OpenFileWorkspaceAsync(WorkspaceTypeId, new FileWorkspaceOpenOptions(
            DepotFileRefs.Create(connection.Id, project, path), $"depot:{connection.Id}:{project}:{path}",
            Path.GetFileName(path), $"Depot / {connection.Name} / {project}", edit, createNew,
            createNew ? $"# {Path.GetFileNameWithoutExtension(path)}\n\n" : "", preview));
}
