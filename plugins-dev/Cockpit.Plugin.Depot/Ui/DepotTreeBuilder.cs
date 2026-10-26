using Cockpit.Plugin.Depot.Contracts;

namespace Cockpit.Plugin.Depot.UI;

internal enum DepotTreeKind
{
    Folder,
    Document,
    Artifact,
}

// `Node` is null on a folder.
internal sealed record DepotTreeItem(string Name, string Path, DepotTreeKind Kind, DepotBrowserNode? Node, IReadOnlyList<DepotTreeItem> Children);

// AC-1568: Depot lists full paths, there is no tree endpoint — the folders are made up here from the slashes.
internal static class DepotTreeBuilder
{
    private sealed class _Folder(string name, string path)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public Dictionary<string, _Folder> Folders { get; } = new(StringComparer.Ordinal);
        public List<DepotBrowserNode> Leaves { get; } = [];

        public IReadOnlyList<DepotTreeItem> Items() =>
        [
            .. Folders.Values.OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)
                .Select(folder => new DepotTreeItem(folder.Name, folder.Path, DepotTreeKind.Folder, null, folder.Items())),
            .. Leaves.OrderBy(leaf => System.IO.Path.GetFileName(leaf.Path), StringComparer.OrdinalIgnoreCase)
                .Select(leaf => new DepotTreeItem(
                    leaf.Path[(leaf.Path.LastIndexOf('/') + 1)..],
                    leaf.Path,
                    leaf.Kind == DepotBrowserKind.Document ? DepotTreeKind.Document : DepotTreeKind.Artifact,
                    leaf,
                    [])),
        ];
    }

    public static IReadOnlyList<DepotTreeItem> Build(IEnumerable<DepotBrowserNode> nodes)
    {
        var root = new _Folder(string.Empty, string.Empty);
        foreach (var node in nodes)
        {
            var parts = node.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var folder = root;
            for (var index = 0; index < parts.Length - 1; index++)
            {
                var path = string.Join('/', parts.Take(index + 1));
                if (!folder.Folders.TryGetValue(parts[index], out var child))
                {
                    child = new _Folder(parts[index], path);
                    folder.Folders[parts[index]] = child;
                }

                folder = child;
            }

            folder.Leaves.Add(node);
        }

        return root.Items();
    }
}
