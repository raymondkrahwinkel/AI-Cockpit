using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace Cockpit.App.Diagnostics;

// AC-1263: Avalonia's own cut-off counts rounds inside one layout pass. The 31-08 freeze never tripped it -- each
// pass ended, and the next render tick found the same work waiting again. Eleven minutes, no layout-loops.log.
// This is the grade above it: a bound across render ticks, judged on samples the freeze alarm already takes.
internal sealed class LayoutLoopGuard
{
    // 3 samples x the 10s sampling interval = a subtree that has made no progress for twenty-plus seconds while
    // the UI thread is already flagged unresponsive. Measured headroom over a heavy healthy pass: see AC-1263.
    public const int DefaultSamplesBeforeCut = 3;

    // 0 turns the net off. The counter-proof runs on it, and an operator who would rather keep the freeze than
    // lose a subtree has a way out that needs no build.
    public const string SamplesEnvironmentVariable = "COCKPIT_LAYOUT_CUTOFF_SAMPLES";

    private readonly int _samplesBeforeCut;

    private Layoutable? _subtree;
    private int _dirtyCount;
    private int _streak;

    public LayoutLoopGuard(int? samplesBeforeCut = null) =>
        _samplesBeforeCut = samplesBeforeCut ?? ConfiguredSamplesBeforeCut();

    public bool Enabled => _samplesBeforeCut > 0;

    // Anything unparseable or negative reads as "not configured": a typo in an environment variable must not
    // silently disarm the net, and must not arm it harder either.
    public static int ConfiguredSamplesBeforeCut() =>
        int.TryParse(Environment.GetEnvironmentVariable(SamplesEnvironmentVariable), out var configured) && configured >= 0
            ? configured
            : DefaultSamplesBeforeCut;

    public void Reset()
    {
        _subtree = null;
        _dirtyCount = 0;
        _streak = 0;
    }

    // The subtree to stop laying out, or null while the pass is still getting somewhere. A sample that shrinks
    // the set, moves to another subtree, or comes up empty is progress and starts the count over.
    public Layoutable? Observe(IReadOnlyList<Layoutable> dirty)
    {
        if (!Enabled)
        {
            return null;
        }

        var subtree = _CuttableAncestorOf(dirty);
        if (subtree is null || dirty.Count < _dirtyCount || !ReferenceEquals(subtree, _subtree))
        {
            _subtree = subtree;
            _dirtyCount = dirty.Count;
            _streak = subtree is null ? 0 : 1;
            return null;
        }

        _dirtyCount = dirty.Count;
        if (++_streak < _samplesBeforeCut)
        {
            return null;
        }

        // Reset rather than latch: if hiding this subtree did not settle the loop, the next set will name a
        // different one, and the count starts again from there rather than cutting the same branch twice.
        Reset();
        return subtree;
    }

    // Hiding is the only public lever that takes a subtree out of layout: an invisible element is not measured,
    // so nothing under it can dirty itself again. It is also the report: the region is visibly gone.
    public static void Cut(Layoutable subtree) => subtree.IsVisible = false;

    private static Layoutable? _CuttableAncestorOf(IReadOnlyList<Layoutable> dirty)
    {
        if (dirty.Count == 0)
        {
            return null;
        }

        var shared = _AncestryOf(dirty[0]);
        for (var index = 1; index < dirty.Count && shared.Count > 1; index++)
        {
            var other = _AncestryOf(dirty[index]);
            var common = 0;
            while (common < shared.Count && common < other.Count && ReferenceEquals(shared[common], other[common]))
            {
                common++;
            }

            shared.RemoveRange(common, shared.Count - common);
        }

        // Never the window itself: a cockpit that is not on screen is worse than a frozen one, and there is
        // nothing left to operate. A loop that wide stays a log line, which the dirty samples already write.
        for (var index = shared.Count - 1; index >= 0; index--)
        {
            if (shared[index] is Layoutable candidate and not TopLevel)
            {
                return candidate;
            }
        }

        return null;
    }

    private static List<Visual> _AncestryOf(Visual element)
    {
        var path = new List<Visual>();
        for (Visual? node = element; node is not null; node = node.GetVisualParent())
        {
            path.Add(node);
        }

        path.Reverse();
        return path;
    }
}
