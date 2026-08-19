using Cockpit.Core.Layout;

namespace Cockpit.Core.Abstractions.Layout;

/// <summary>
/// Loads and persists a window's <see cref="WindowBounds"/> in <c>cockpit.json</c>, keyed per window
/// (<c>"main"</c>, <c>"assistant"</c> — AC-866). Returns null when nothing was ever saved for that key, so the
/// caller falls back to the default centered size.
/// </summary>
public interface IWindowBoundsStore
{
    Task<WindowBounds?> LoadAsync(string key, CancellationToken cancellationToken = default);

    Task SaveAsync(string key, WindowBounds bounds, CancellationToken cancellationToken = default);
}
