using Avalonia;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;

namespace Cockpit.App.Diagnostics;

// AC-1236: twelve cut-offs left no way to say which control looped -- Avalonia's stack stops at MediaContext and
// names no element. A pass cut mid-flight leaves its unfinished elements invalid, so reading that at the throw
// costs nothing until one happens, unlike a per-measure trace on the UI thread this exists to keep free.
internal static class LayoutLoopReport
{
    private const int MaxNodes = 20_000;
    private const int MaxPending = 200;
    private const int MaxLines = 10;

    // AC-1254: the shared log drops lines under contention and says nothing about it, and a cut-off writes at the
    // worst possible moment for that. A file nothing else appends to gives the report a second, uncontended copy.
    private const long MaxRecordBytes = 256 * 1024;

    internal static string RecordPathFor(string logPath) =>
        Path.Combine(Path.GetDirectoryName(logPath) ?? ".", "layout-loops.log");

    // Never throws: it runs inside the global unhandled-exception net, where a second failure would bury the first.
    public static void Record(IEnumerable<Visual> roots, string recordPath, ILogger? logger)
    {
        try
        {
            Record(Collect(roots), "layout loop cut off", recordPath, logger);
        }
        catch (Exception failure)
        {
            logger?.LogWarning("Could not read the layout tree after a cut-off: {Failure}", failure);
        }
    }

    // AC-1263: the same line for a set already read off the tree, so the guard's cut lands in the record file
    // in the shape every earlier episode is written in, and two of them can be laid side by side.
    public static void Record(IReadOnlyList<Layoutable> dirty, string headline, string recordPath, ILogger? logger)
    {
        var elements = Group(dirty);
        var line = $"{DateTimeOffset.Now:O} {headline}, {elements.Count} element(s) still in layout: "
            + (elements.Count == 0 ? "(none)" : string.Join(" | ", elements));

        logger?.LogWarning("{Report}", line);
        _Append(recordPath, line);
    }

    // AC-1263: the elements themselves, not their descriptions -- the guard has to act on the subtree they
    // sit under, and a second walk to find it would read a tree that has moved on since the first.
    public static IReadOnlyList<Layoutable> Collect(IEnumerable<Visual> roots)
    {
        var dirty = new List<Layoutable>();
        var unvisited = new Stack<Visual>(roots);
        var visited = 0;

        while (dirty.Count < MaxPending && visited++ < MaxNodes && unvisited.TryPop(out var visual))
        {
            // AC-1262: a hidden subtree is never measured, so its elements stay invalid for as long as it stays
            // hidden. Reading that as stuck named nine bystanders on 31-08, and once AC-1263's guard hides a
            // runaway subtree it would keep re-reporting the very subtree it just took out of layout.
            if (!visual.IsVisible)
            {
                continue;
            }

            if (visual is Layoutable element && (!element.IsMeasureValid || !element.IsArrangeValid))
            {
                dirty.Add(element);
            }

            foreach (var child in visual.GetVisualChildren())
            {
                unvisited.Push(child);
            }
        }

        return dirty;
    }

    // Elements the cut pass never finished, grouped by a key that carries no run-specific detail and no data
    // value, so two reports from different episodes can be laid side by side and the repeat offender read off.
    public static IReadOnlyList<string> Describe(IEnumerable<Visual> roots) => Group(Collect(roots));

    // The grouping half on its own, for a caller that already holds the set.
    public static IReadOnlyList<string> Group(IReadOnlyList<Layoutable> dirty)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var element in dirty)
        {
            var key = _Describe(element);
            counts[key] = counts.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }

        // Deepest first: a leaf entry names the control itself, a shallow one only the region it sits in.
        return counts
            .OrderByDescending(entry => entry.Value)
            .ThenByDescending(entry => entry.Key.Count(character => character == '>'))
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(MaxLines)
            .Select(entry => entry.Value == 1 ? entry.Key : $"{entry.Value}x {entry.Key}")
            .ToArray();
    }

    // Type names, the XAML-authored x:Name and the view-model type only. A DataContext in the running cockpit holds
    // transcript text, paths and tokens, so nothing is read off it but its type.
    private static string _Describe(Layoutable element)
    {
        var path = new List<string>();
        for (Visual? node = element; node is not null; node = node.GetVisualParent())
        {
            path.Add(node is StyledElement { Name: { Length: > 0 } name }
                ? $"{node.GetType().Name}#{name}"
                : node.GetType().Name);
        }

        path.Reverse();
        var stage = element.IsMeasureValid ? "arrange" : "measure";
        var model = element.DataContext?.GetType().Name;
        return model is null
            ? $"{string.Join(" > ", path)} [{stage}]"
            : $"{string.Join(" > ", path)} [{stage}, {model}]";
    }

    private static void _Append(string path, string line)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Kept across runs, unlike the app log: two quick restarts after a freeze are the normal reaction (AC-1113)
        // and would otherwise discard the very episode this file exists to hold.
        var existing = new FileInfo(path);
        if (!existing.Exists || existing.Length < MaxRecordBytes)
        {
            File.AppendAllText(path, line + Environment.NewLine);
        }
    }
}
