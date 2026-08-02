namespace Cockpit.Core.Diagnostics;

// When to warn that the cockpit and its sessions together are using enough memory for the operating system to start
// killing things.
//
// A Claude session is 300–700 MB of Node; three of them is more memory than the whole cockpit. So the number worth
// watching is not ours, it is the tree's — and this warning is the one thing that turns "the app disappeared" into
// "you were warned, and you could close a session".
//
// An earlier version of this comment explained the macOS behaviour by *coalitions* — that macOS charges a
// process for everything it spawns and kills the coalition leader. That was wrong, and it is corrected here rather
// than deleted, because it is the kind of plausible story that gets re-derived. macOS does not use the jetsam bands
// for this at all (they are iOS); under memory pressure it runs `no_paging_space_action()`, which kills the
// process holding the most compressed pages — "killing largest compressed process", in the kernel's own words.
// Nobody is charged for their children, and the leader is not singled out. Which also means the obvious mitigation
// (spawn the sessions outside our coalition) would have bought exactly nothing.
//
// The lever that does exist is `POSIX_SPAWN_PCONTROL_KILL`: a child can be spawned volunteering itself as the
// one to kill when memory runs out. Not done yet — and it should not be, until a jetsam log actually shows the
// cockpit dying this way rather than something else entirely. See the macOS section of Memory/Cockpit/Todo.md.
//
// The rule below is written to be ignorable exactly once. It warns on the way up, and it does not warn again until
// memory has fallen well back — a warning that repeats every ten seconds while you are deciding what to close is a
// warning you turn off, and then it is not there on the day it matters.
public static class MemoryPressure
{
    // Warn when the cockpit's whole tree passes this share of the machine's memory. Two thirds: enough headroom left to act, close enough to trouble to mean something.
    public const double WarnAtShare = 0.66;

    // Stay quiet again once it has fallen back below this share — not the moment it dips under the line, or a session that breathes would warn you twice a minute.
    public const double CalmAtShare = 0.55;

    // Below this, say nothing whatever the share: on a machine with 4 GB, two thirds is reached by opening a browser, and a warning that fires on an idle cockpit teaches you to ignore it.
    public const long FloorBytes = 3L * 1024 * 1024 * 1024;

    // How the number should read in the status bar. The colour arrives before the warning does — a figure turning
    // amber while you work is a thing you can act on quietly; a toast is an interruption, and it is only worth one
    // when the machine is actually close to killing something.
    public static MemoryPressureLevel Level(long usedBytes, long totalBytes)
    {
        if (totalBytes <= 0 || usedBytes < FloorBytes)
        {
            return MemoryPressureLevel.Calm;
        }

        var share = (double)usedBytes / totalBytes;

        return share >= WarnAtShare
            ? MemoryPressureLevel.High
            : share >= CalmAtShare ? MemoryPressureLevel.Elevated : MemoryPressureLevel.Calm;
    }

    // Whether to warn now. `warned` is whether the operator has already been told and not yet been
    // let off the hook — the caller keeps that between calls.
    //
    // `usedBytes`: What the cockpit's tree is using: itself, and every session it spawned.
    // `totalBytes`: The machine's memory. Zero when it could not be read, which means no warning: a share of an unknown total is not a fact.
    // `warned`: Whether a warning is already standing.
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

// `Warn`: Tell the operator now.
// `Warned`: What to remember for the next sample.
public sealed record MemoryPressureDecision(bool Warn, bool Warned);

// How the memory figure in the status bar should read — quietly, or as something to look at.
public enum MemoryPressureLevel
{
    // Nothing to see.
    Calm,

    // Climbing. Worth a colour, not worth a sentence: the operator can decide to close something before anyone asks them to.
    Elevated,

    // The point at which the warning fires, and at which macOS starts thinking about killing us.
    High,
}
