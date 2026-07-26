namespace Cockpit.Core.Sessions;

/// <summary>
/// What the plugins between them give one starting session (AC-165), already merged and already scrubbed — the
/// shape the launch routes fold into the environment they were building anyway.
/// <para>
/// Resolved once per launch rather than per route, so a session gets the same answer whichever door it came
/// through — the same reason <see cref="SessionStartDefaults"/> is one resolver instead of a rule repeated per
/// caller. A record rather than the dictionary itself, so it grows with the plugin-facing contribution it mirrors.
/// </para>
/// </summary>
/// <param name="EnvironmentVariables">Variables to put in the session's process, with every host-controlled key already dropped.</param>
public sealed record SessionResources(IReadOnlyDictionary<string, string> EnvironmentVariables)
{
    /// <summary>Nothing contributed — what a session gets when no plugin has anything for it, which is the ordinary case.</summary>
    public static SessionResources Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Whether anything was contributed, so a caller can skip its merge entirely for the common case.</summary>
    public bool IsEmpty => EnvironmentVariables.Count == 0;
}
