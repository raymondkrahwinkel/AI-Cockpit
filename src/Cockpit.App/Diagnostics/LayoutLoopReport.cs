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
            var elements = Describe(roots);
            var line = $"{DateTimeOffset.Now:O} layout loop cut off, {elements.Count} element(s) still in layout: "
                + (elements.Count == 0 ? "(none)" : string.Join(" | ", elements));

            logger?.LogWarning("{Report}", line);
            _Append(recordPath, line);
        }
        catch (Exception failure)
        {
            logger?.LogWarning("Could not read the layout tree after a cut-off: {Failure}", failure);
        }
    }

    // Elements the cut pass never finished, grouped by a key that carries no run-specific detail and no data
    // value, so two reports from different episodes can be laid side by side and the repeat offender read off.
    public static IReadOnlyList<string> Describe(IEnumerable<Visual> roots)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var unvisited = new Stack<Visual>(roots);
        var visited = 0;
        var collected = 0;

        while (collected < MaxPending && visited++ < MaxNodes && unvisited.TryPop(out var visual))
        {
            // AC-1262: an invisible subtree is never measured, so everything under it stays invalid for good and
            // named every report as a suspect -- a LoginFlowView behind IsVisible did, with no login flow running.
            if (!visual.IsVisible)
            {
                continue;
            }

            if (visual is Layoutable element && (!element.IsMeasureValid || !element.IsArrangeValid))
            {
                var key = _Describe(element);
                counts[key] = counts.TryGetValue(key, out var seen) ? seen + 1 : 1;
                collected++;
            }

            foreach (var child in visual.GetVisualChildren())
            {
                unvisited.Push(child);
            }
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
