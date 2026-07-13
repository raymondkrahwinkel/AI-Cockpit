using Cockpit.Core.Diagnostics;

namespace Cockpit.Core.Abstractions.Diagnostics;

/// <summary>
/// Reads what a process is using right now (#78). Implemented per platform, because "and everything it spawned"
/// is the interesting part and no cross-platform API gives you that: a session is a <c>claude</c> process, but
/// the CPU you care about is the build it just started.
/// </summary>
public interface IResourceSampler
{
    /// <summary>The cockpit's own process.</summary>
    ResourceSample SampleSelf();

    /// <summary>The process with this id <em>and its descendants</em>. Returns <see cref="ResourceSample.None"/> when it is gone — a session that just exited is not an error.</summary>
    ResourceSample SampleTree(int processId);

    /// <summary>Whether this platform can actually see a process's descendants. False means the numbers cover only the process itself, and the UI must say so rather than quietly under-reporting.</summary>
    bool CountsDescendants { get; }
}
