using Cockpit.Core.Terminal;

namespace Cockpit.Core.Abstractions.Terminal;

/// <summary>
/// Loads and persists <see cref="TerminalSettings"/> in <c>cockpit.json</c> (the same config file the
/// profiles and notifications live in). When no settings were ever saved, <see cref="LoadAsync"/>
/// returns the defaults.
/// </summary>
public interface ITerminalSettingsStore
{
    Task<TerminalSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(TerminalSettings settings, CancellationToken cancellationToken = default);
}
