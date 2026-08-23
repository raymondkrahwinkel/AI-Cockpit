namespace Cockpit.Core.Abstractions.Verify;

/// <summary>
/// The verify tool's window into a live session (AC-86), host-side over the running session panels. Reads where a
/// session runs and pushes the rendered <em>screenshot</em> as a user turn — the text already travels on the tool
/// result, so this exists only for the image. A TTY, non-vision, or headless session reports "no such session" instead.
/// </summary>
public interface IVerifySessionGateway
{
    /// <summary>
    /// The working directory the session with <paramref name="paneId"/> is running in, or null when no such live session is open.
    /// </summary>
    string? GetWorkingDirectory(string paneId);

    /// <summary>
    /// Pushes the verify <paramref name="screenshotPng"/> into the session with <paramref name="paneId"/> as a user
    /// turn, captioned with <paramref name="caption"/>. Returns true only when the screenshot was actually shown —
    /// false when no such live session is open, or its provider cannot display images.
    /// </summary>
    Task<bool> FeedResultAsync(string paneId, string caption, byte[] screenshotPng, CancellationToken cancellationToken = default);
}
