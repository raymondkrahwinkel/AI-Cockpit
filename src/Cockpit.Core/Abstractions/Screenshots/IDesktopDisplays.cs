namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What the desktop says its displays are (AC-326) — asked of the UI framework, which is the only thing in the
/// process that already tracks them, and answered for a capture that cannot work it out for itself.
/// </summary>
/// <remarks>
/// Lives here rather than in Infrastructure because the answer comes from Avalonia, Avalonia hangs off the app's
/// own window, and Infrastructure does not reference a UI framework — so the interface sits here and the
/// implementation with the views. The Linux capture is the caller — Windows and macOS enumerate displays through the same API
/// that reads their pixels, which is a stronger guarantee than this can offer and so is preferred there.
/// </remarks>
public interface IDesktopDisplays
{
    /// <summary>
    /// The displays the desktop currently reports, or an empty list when it reports none — a headless or
    /// design-time process, where a capture has nothing to map onto and must say so rather than guess.
    /// </summary>
    Task<IReadOnlyList<DesktopDisplay>> EnumerateAsync(CancellationToken cancellationToken = default);
}
