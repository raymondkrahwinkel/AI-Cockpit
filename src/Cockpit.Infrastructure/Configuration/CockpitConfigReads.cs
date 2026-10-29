namespace Cockpit.Infrastructure.Configuration;

// AC-1892: how often this process parsed `cockpit.json` from disk — a count only, never a value. Public for
// the one start-up log line in Cockpit.App, which cannot see this assembly's internals.
public static class CockpitConfigReads
{
    private static long _parses;

    public static long Parses => Interlocked.Read(ref _parses);

    internal static void NoteParse() => Interlocked.Increment(ref _parses);
}
