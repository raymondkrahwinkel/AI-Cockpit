namespace Cockpit.Core.Sessions;

/// <summary>
/// Which conversation a session picks up when it starts: a fresh one, the most recent one in its working
/// directory, or a specific one by id. Resuming is what makes a crashed or closed cockpit survivable — the
/// conversation lives in the provider's own history, so the work does not have to start over.
/// </summary>
public enum SessionResumeMode
{
    /// <summary>A new conversation.</summary>
    New,

    /// <summary>The most recent conversation in the session's working directory (the CLI's <c>--continue</c>).</summary>
    MostRecent,

    /// <summary>A specific conversation, named by its id (the CLI's <c>--resume &lt;id&gt;</c>).</summary>
    BySessionId,
}

/// <summary>
/// What a starting session should resume, chosen in the New-session dialog. Only providers that keep a
/// conversation history of their own can honour this — see <see cref="SessionCapabilities.SupportsResume"/>.
/// </summary>
/// <param name="Mode">Fresh conversation, most recent one, or one by id.</param>
/// <param name="SessionId">The conversation to resume when <paramref name="Mode"/> is <see cref="SessionResumeMode.BySessionId"/>; ignored otherwise.</param>
public sealed record SessionResume(SessionResumeMode Mode, string? SessionId = null)
{
    public static SessionResume New { get; } = new(SessionResumeMode.New);

    public static SessionResume MostRecent { get; } = new(SessionResumeMode.MostRecent);

    public static SessionResume BySessionId(string sessionId) => new(SessionResumeMode.BySessionId, sessionId);

    /// <summary>True when this asks to pick up an existing conversation rather than start a new one, and says which one usably.</summary>
    public bool IsResuming => Mode switch
    {
        SessionResumeMode.MostRecent => true,
        SessionResumeMode.BySessionId => !string.IsNullOrWhiteSpace(SessionId),
        _ => false,
    };
}
