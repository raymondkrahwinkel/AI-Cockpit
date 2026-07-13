using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Abstractions.Diagnostics;

/// <summary>
/// Reads the machine's process table (#78) — every platform its own way (<c>/proc</c> on Linux, <c>ps</c> on
/// macOS, WMI on Windows), because none of them agree and .NET exposes no parent-process id at all. What comes
/// back is the same everywhere, so everything above this line is written and tested once.
/// </summary>
public interface IProcessTableReader
{
    IReadOnlyList<ProcessRow> Read();
}
