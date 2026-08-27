using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Tests.Diagnostics;

public sealed class CachedProcessTableReaderTests
{
    [Fact]
    public void OneSnapshot_ServesEveryRootWithoutReindexing()
    {
        var reader = new CachedProcessTableReader(new FixedProcessTable(Enumerable.Range(1, 400).Select(id => new ProcessRow(id, 0, TimeSpan.Zero, 1024)).ToArray()));
        var rows = reader.Read();

        foreach (var processId in Enumerable.Range(1, 8))
        {
            ProcessTree.Sum(rows, processId);
        }

        Assert.Equal(1, reader.SnapshotsBuilt);
    }

    private sealed class FixedProcessTable(IReadOnlyList<ProcessRow> rows) : IProcessTableReader
    {
        public IReadOnlyList<ProcessRow> Read() => rows;
    }
}
