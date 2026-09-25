namespace Cockpit.Plugin.SessionReview;

// A node of the review tree (AC-578): a folder when `File` is null, otherwise a changed file.
internal sealed class TreeNode
{
    public required string Label { get; init; }

    // The file this node stands for, or null when the node is a folder.
    public FileDiff? File { get; init; }

    public List<TreeNode> Children { get; } = [];
}

// Builds the folder tree the review panel shows on the left (AC-578) out of the changed files' paths.
internal static class FileTree
{
    // Nests the files under their folders, collapsing any run of folders that holds nothing but one more folder
    // into a single node (`src/Cockpit.App/Controls` rather than three rows). Without that, a .NET repository
    // spends the whole panel on empty levels before the first file appears.
    public static IReadOnlyList<TreeNode> Build(IEnumerable<FileDiff> files)
    {
        var root = new _Folder();
        foreach (var file in files)
        {
            var parts = file.Path.Split('/');
            var folder = root;
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (!folder.Folders.TryGetValue(parts[i], out var child))
                {
                    folder.Folders[parts[i]] = child = new _Folder();
                }

                folder = child;
            }

            folder.Files.Add(file);
        }

        return _Convert(root);
    }

    private static List<TreeNode> _Convert(_Folder folder)
    {
        var nodes = new List<TreeNode>();
        foreach (var (name, child) in folder.Folders)
        {
            var label = name;
            var current = child;
            while (current.Files.Count == 0 && current.Folders.Count == 1)
            {
                var only = current.Folders.First();
                label = $"{label}/{only.Key}";
                current = only.Value;
            }

            var node = new TreeNode { Label = label };
            node.Children.AddRange(_Convert(current));
            nodes.Add(node);
        }

        // Folders first, then this level's own files — the same order a file explorer uses.
        nodes.AddRange(folder.Files
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new TreeNode { Label = f.Name, File = f }));

        return nodes;
    }

    private sealed class _Folder
    {
        public SortedDictionary<string, _Folder> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<FileDiff> Files { get; } = [];
    }
}
