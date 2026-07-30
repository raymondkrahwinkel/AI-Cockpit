namespace Cockpit.Plugin.Autopilot.Tests;

/// <summary>Kahn's algorithm over an epic's "depends on" links (AC-346), with a stable tie-break on issue id.</summary>
public class EpicSubTopologicalOrderTests
{
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Deps(params (string Id, string[] DependsOn)[] entries) =>
        entries.ToDictionary(entry => entry.Id, IReadOnlyList<string> (entry) => entry.DependsOn, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Resolve_WithNoDependencies_OrdersByIdAscending()
    {
        var order = EpicSubTopologicalOrder.Resolve(["C", "A", "B"], Deps());

        Assert.Equal(["A", "B", "C"], order);
    }

    [Fact]
    public void Resolve_TheAc325Chain_RespectsEveryDependsOnEdge()
    {
        // [0] -> [a][b][c] -> [d] -> [e][f] -> [g]
        var ids = new[] { "0", "a", "b", "c", "d", "e", "f", "g" };
        var deps = Deps(
            ("a", ["0"]), ("b", ["0"]), ("c", ["0"]),
            ("d", ["a", "b", "c"]),
            ("e", ["d"]), ("f", ["d"]),
            ("g", ["e", "f"]));

        var order = EpicSubTopologicalOrder.Resolve(ids, deps);

        Assert.Equal(8, order.Count);
        var index = order.Select((id, position) => (id, position)).ToDictionary(pair => pair.id, pair => pair.position);
        Assert.True(index["0"] < index["a"]);
        Assert.True(index["0"] < index["b"]);
        Assert.True(index["0"] < index["c"]);
        Assert.True(index["a"] < index["d"]);
        Assert.True(index["b"] < index["d"]);
        Assert.True(index["c"] < index["d"]);
        Assert.True(index["d"] < index["e"]);
        Assert.True(index["d"] < index["f"]);
        Assert.True(index["e"] < index["g"]);
        Assert.True(index["f"] < index["g"]);
        // Among "a"/"b"/"c" — free to run in any order once "0" is placed — the deterministic tie-break is id order.
        Assert.True(index["a"] < index["b"]);
        Assert.True(index["b"] < index["c"]);
    }

    [Fact]
    public void Resolve_ADependencyOutsideTheGivenSet_IsIgnored()
    {
        // A sub can depend on an issue that is not itself a sub of this epic (a cross-epic link) — Resolve only orders
        // among the ids it was given, so an unknown dependency must not stall it.
        var order = EpicSubTopologicalOrder.Resolve(["A", "B"], Deps(("A", ["OUTSIDE-1"])));

        Assert.Equal(["A", "B"], order);
    }

    [Fact]
    public void Resolve_ACyclicDependsOnChain_StillTerminatesDeterministically()
    {
        var deps = Deps(("A", ["B"]), ("B", ["A"]));

        var first = EpicSubTopologicalOrder.Resolve(["A", "B"], deps);
        var second = EpicSubTopologicalOrder.Resolve(["A", "B"], deps);

        Assert.Equal(2, first.Count);
        Assert.Equal(first, second);
    }
}
