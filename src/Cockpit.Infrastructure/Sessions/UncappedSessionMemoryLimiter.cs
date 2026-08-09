using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Sessions;

// macOS (and any platform that is neither Windows nor Linux): no cap, said out loud rather than pretended (AC-661,
// criterion 3). Same accepted-blind-spot status as AC-57 — there is no Mac here to verify anything on, and an
// unverified native memory limit is worse than a documented gap, because it reads as protection that is not there.
//
// The approach when a Mac is available: macOS has no cgroups and no job objects. The nearest equivalents are
//  - `setrlimit(RLIMIT_AS)` inherited across `fork`, capping each process in the tree individually rather than the
//    tree as a whole. Cheap, but address space is not memory: a JS or .NET runtime reserves far more than it commits,
//    so a cap that catches a runaway build also strangles a healthy `claude`. It would need calibrating on real
//    hardware, which is exactly what cannot be done here.
//  - `task_policy` / Jetsam bands (`memorystatus_control`), which is what the OS itself uses to pick a victim under
//    pressure. Private API, entitlement-gated, and version-sensitive.
// Neither is written down as code until it can be run: the cockpit would report a cap it never enforced.
internal sealed class UncappedSessionMemoryLimiter(ILogger<UncappedSessionMemoryLimiter> logger) : ISessionMemoryLimiter
{
    public string? Mechanism => null;

    public IDisposable? Apply(int processId, long capBytes)
    {
        logger.LogDebug("Session memory cap: not enforced on this platform; session {ProcessId} runs uncapped.", processId);
        return null;
    }
}
