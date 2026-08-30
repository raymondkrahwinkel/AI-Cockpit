using Cockpit.Core.Abstractions.Diagnostics;
using Cockpit.Core.Diagnostics;

namespace Cockpit.App.ViewTests;

internal sealed class FixedProcessTable(IReadOnlyList<ProcessRow> rows) : IProcessTableReader
{
    public IReadOnlyList<ProcessRow> Read() => rows;
}
