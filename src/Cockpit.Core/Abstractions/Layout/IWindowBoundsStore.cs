using Cockpit.Core.Layout;

namespace Cockpit.Core.Abstractions.Layout;

/// <summary>
/// Loads and persists the main window's <see cref="WindowBounds"/> in <c>cockpit.json</c>. Returns null when
/// nothing was ever saved, so the caller falls back to the default centered size.
/// </summary>
public interface IWindowBoundsStore
{
    Task<WindowBounds?> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WindowBounds bounds, CancellationToken cancellationToken = default);
}
