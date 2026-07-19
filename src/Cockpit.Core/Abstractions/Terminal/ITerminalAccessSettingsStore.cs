using Cockpit.Core.Terminal;

namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>
/// Loads and persists the terminal-access master switch (AC-34) in <c>cockpit.json</c>. When nothing was ever saved,
/// <see cref="LoadAsync"/> returns <see cref="TerminalAccessSettings.Default"/> (off) — the feature is opt-in.
/// </summary>
public interface ITerminalAccessSettingsStore
{
    Task<TerminalAccessSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TerminalAccessSettings settings, CancellationToken cancellationToken = default);
}
