using Cockpit.Plugin.Depot.Contracts;
using Cockpit.Plugins.Abstractions.UI;
using Cockpit.Plugins.Abstractions.UI.Files;

namespace Cockpit.Plugin.Depot.UI;

internal static class DepotMarkdownFiles
{
    public const string WorkspaceTypeId = "depot.markdown";
    public static bool IsMarkdown(DepotBrowserNode node) => node.Kind == DepotBrowserKind.Document
        && (node.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || node.Path.EndsWith(".markdown", StringComparison.OrdinalIgnoreCase));

    public static Task OpenAsync(ICockpitUiHost host, DepotConnectionRegistration connection,
        string project, string path, bool edit, bool createNew = false) =>
        host.OpenFileWorkspaceAsync(WorkspaceTypeId, new FileWorkspaceOpenOptions(
            DepotFileRefs.Create(connection.Id, project, path), $"depot:{connection.Id}:{project}:{path}",
            Path.GetFileName(path), $"Depot / {connection.Name} / {project}", edit, createNew,
            createNew ? $"# {Path.GetFileNameWithoutExtension(path)}\n\n" : ""));
}
