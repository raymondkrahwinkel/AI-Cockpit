namespace Cockpit.Core.Diagnostics;

/// <summary>
/// When to warn that the cockpit and its sessions together are using enough memory for the operating system to start
/// killing things — and, on macOS, to kill <em>us</em>.
/// <para>
/// macOS groups a process and everything it spawns into one <b>coalition</b>, and its memory killer (jetsam) counts
/// and kills at that level: the Claude processes we start are charged to the cockpit, and when it fires, the coalition
/// leader — the cockpit — is what dies, taking every session with it. A Claude session is 300–700 MB of Node; three of
/// them is more memory than the whole cockpit. So the number worth watching is not ours, it is the tree's, and this is
/// the one thing that turns "the app disappeared" into "you were warned, and you could close a session".
/// </para>
/// <para>
/// The rule below is written to be ignorable exactly once. It warns on the way up, and it does not warn again until
/// memory has fallen well back — a warning that repeats every ten seconds while you are deciding what to close is a
/// warning you turn off, and then it is not there on the day it matters.
/// </para>
/// </summary>
public static class MemoryPressure
{
    /// <summary>Warn when the cockpit's whole tree passes this share of the machine's memory. Two thirds: enough headroom left to act, close enough to trouble to mean something.</summary>
    public const double WarnAtShare = 0.66;

    /// <summary>Stay quiet again once it has fallen back below this share — not the moment it dips under the line, or a session that breathes would warn you twice a minute.</summary>
    public const double CalmAtShare = 0.55;

    /// <summary>Below this, say nothing whatever the share: on a machine with 4 GB, two thirds is reached by opening a browser, and a warning that fires on an idle cockpit teaches you to ignore it.</summary>
    public const long FloorBytes = 3L * 1024 * 1024 * 1024;

    /// <summary>
    /// Whether to warn now. <paramref name="warned"/> is whether the operator has already been told and not yet been
    /// let off the hook — the caller keeps that between calls.
    /// </summary>
    /// <param name="usedBytes">What the cockpit's tree is using: itself, and every session it spawned.</param>
    /// <param name="totalBytes">The machine's memory. Zero when it could not be read, which means no warning: a share of an unknown total is not a fact.</param>
    /// <param name="warned">Whether a warning is already standing.</param>
    public static MemoryPressureDecision Decide(long usedBytes, long totalBytes, bool warned)
    {
        if (totalBytes <= 0 || usedBytes <= 0)
        {
            return new MemoryPressureDecision(false, warned);
        }

        var share = (double)usedBytes / totalBytes;

        if (!warned && share >= WarnAtShare && usedBytes >= FloorBytes)
        {
            return new MemoryPressureDecision(true, true);
        }

        if (warned && share <= CalmAtShare)
        {
            // Let off the hook: the next time it climbs, it is worth saying again.
            return new MemoryPressureDecision(false, false);
        }

        return new MemoryPressureDecision(false, warned);
    }
}

/// <param name="Warn">Tell the operator now.</param>
/// <param name="Warned">What to remember for the next sample.</param>
public sealed record MemoryPressureDecision(bool Warn, bool Warned);
