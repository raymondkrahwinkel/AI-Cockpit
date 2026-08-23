namespace Cockpit.Core.Sessions;

// AC-165: what the plugins between them give one starting session, already merged and scrubbed. Resolved
// once per launch rather than per route — same reasoning as `SessionStartDefaults` — so a session gets the
// same answer whichever door it came through. `EnvironmentVariables` never carries a host-controlled key.
public sealed record SessionResources(IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    // Nothing contributed — what a session gets when no plugin has anything for it, which is the ordinary case.
    public static SessionResources Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    // Whether anything was contributed, so a caller can skip its merge entirely for the common case.
    public bool IsEmpty => EnvironmentVariables.Count == 0;
}
