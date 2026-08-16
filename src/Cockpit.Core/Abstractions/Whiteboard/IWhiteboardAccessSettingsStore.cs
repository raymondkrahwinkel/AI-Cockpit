using Cockpit.Core.Whiteboard;

namespace Cockpit.Core.Abstractions.Whiteboard;

/// <summary>
/// Loads and persists the whiteboard-access master switch (AC-823) in <c>cockpit.json</c>. When nothing was ever
/// saved, <see cref="LoadAsync"/> returns <see cref="WhiteboardAccessSettings.Default"/> (off) — the feature is
/// opt-in. Mirrors <c>IDiagramAccessSettingsStore</c> (AC-810).
/// </summary>
public interface IWhiteboardAccessSettingsStore
{
    Task<WhiteboardAccessSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(WhiteboardAccessSettings settings, CancellationToken cancellationToken = default);
}
