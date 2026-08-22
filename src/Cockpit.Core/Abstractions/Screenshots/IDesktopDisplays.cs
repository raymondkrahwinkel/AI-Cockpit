namespace Cockpit.Core.Abstractions.Screenshots;

/// <summary>
/// What the desktop says its displays are (AC-326) — asked of the UI framework, the only thing in-process that already
/// tracks them. Lives here rather than Infrastructure because the answer comes from Avalonia, and Infrastructure
/// references no UI framework. Windows/macOS prefer enumerating displays through the same API that reads their pixels.
/// </summary>
public interface IDesktopDisplays
{
    /// <summary>
    /// The displays the desktop currently reports, or an empty list when it reports none — a headless or
    /// design-time process, where a capture has nothing to map onto and must say so rather than guess.
    /// </summary>
    Task<IReadOnlyList<DesktopDisplay>> EnumerateAsync(CancellationToken cancellationToken = default);
}
