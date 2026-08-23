namespace Cockpit.Core.Sessions;

// Which conversation a session picks up when it starts: a fresh one, the most recent one in its working
// directory, or a specific one by id. Resuming is what makes a crashed or closed cockpit survivable — the
// conversation lives in the provider's own history, so the work does not have to start over.
public enum SessionResumeMode
{
    // A new conversation.
    New,

    // The most recent conversation in the session's working directory (the CLI's `--continue`).
    MostRecent,

    // A specific conversation, named by its id (the CLI's `--resume &lt;id&gt;`).
    BySessionId,
}

// What a starting session should resume, chosen in the New-session dialog. Only providers that keep a
// conversation history of their own can honour this — see `SessionCapabilities.SupportsResume`. `SessionId`
// applies only when `Mode` is `SessionResumeMode.BySessionId`; ignored otherwise.
public sealed record SessionResume(SessionResumeMode Mode, string? SessionId = null)
{
    public static SessionResume New { get; } = new(SessionResumeMode.New);

    public static SessionResume MostRecent { get; } = new(SessionResumeMode.MostRecent);

    public static SessionResume BySessionId(string sessionId) => new(SessionResumeMode.BySessionId, sessionId);

    // True when this asks to pick up an existing conversation rather than start a new one, and says which one usably.
    public bool IsResuming => Mode switch
    {
        SessionResumeMode.MostRecent => true,
        SessionResumeMode.BySessionId => !string.IsNullOrWhiteSpace(SessionId),
        _ => false,
    };
}
