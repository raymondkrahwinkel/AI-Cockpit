using Cockpit.Core.Diagrams;

namespace Cockpit.Core.Abstractions.Diagrams;

/// <summary>
/// Loads and persists the diagram-access master switch (AC-810) in <c>cockpit.json</c>. When nothing was ever
/// saved, <see cref="LoadAsync"/> returns <see cref="DiagramAccessSettings.Default"/> (off) — the feature is
/// opt-in. Mirrors <c>ITerminalAccessSettingsStore</c> (AC-34).
/// </summary>
public interface IDiagramAccessSettingsStore
{
    Task<DiagramAccessSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DiagramAccessSettings settings, CancellationToken cancellationToken = default);
}
