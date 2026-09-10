using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

internal sealed class FixedProcessTable(IReadOnlyList<ProcessRow> rows) : IProcessTableReader
{
    // Settable so a test can take a second sample of a machine that moved on (AC-1310).
    public IReadOnlyList<ProcessRow> Rows { get; set; } = rows;

    public IReadOnlyList<ProcessRow> Read() => Rows;
}
