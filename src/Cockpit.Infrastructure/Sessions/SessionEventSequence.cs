namespace Cockpit.Infrastructure.Sessions;

// Outside the generic session host: every prompt type and backend event shares this counter.
internal static class SessionEventSequence
{
    private static long _last;

    public static long Next() => Interlocked.Increment(ref _last);
}
