namespace Cockpit.Core.Abstractions.Verify;

/// <summary>
/// The verify tool's window into a live session (AC-86), implemented host-side over the running session panels. It
/// reads where a session is running — to find that project's runner — and pushes the rendered <em>screenshot</em>
/// into it as a user turn. The text snapshot travels on the tool result instead (every provider reads that), so this
/// exists only for the image a tool result cannot carry: a vision-capable SDK session shows it, a TTY session or a
/// non-vision provider ignores it. A headless run has no interactive session, so both members report "no such
/// session" there.
/// </summary>
public interface IVerifySessionGateway
{
    /// <summary>The working directory the session with <paramref name="paneId"/> is running in, or null when no such live session is open.</summary>
    string? GetWorkingDirectory(string paneId);

    /// <summary>
    /// Pushes the verify <paramref name="screenshotPng"/> into the session with <paramref name="paneId"/> as a user
    /// turn, captioned with <paramref name="caption"/>. Returns true only when the screenshot was actually shown —
    /// false when no such live session is open, or its provider cannot display images.
    /// </summary>
    Task<bool> FeedResultAsync(string paneId, string caption, byte[] screenshotPng, CancellationToken cancellationToken = default);
}
