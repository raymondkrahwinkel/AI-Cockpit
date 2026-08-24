namespace Cockpit.Core.Diagnostics;

// AC-1013: Warns when the cockpit+sessions tree nears the OS's memory-kill threshold (a Claude session is
// 300-700 MB; three exceed the whole cockpit); fires once on the way up, stays quiet till usage falls well back
// so it doesn't nag every ten seconds. Was: macOS-coalitions explanation (wrong, corrected) & POSIX_SPAWN_PCONTROL_KILL — see Memory/Cockpit/Todo.md.
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

    // AC-1013: Whether to warn now; `warned` tracks whether the operator has already been told and not yet let
    // off the hook (caller keeps this between calls). `totalBytes` of 0 means "unreadable" and suppresses the
    // warning, since a share of an unknown total is not a fact.
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
