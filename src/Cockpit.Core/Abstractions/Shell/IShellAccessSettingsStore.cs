using Cockpit.Core.Shell;

namespace Cockpit.Core.Abstractions.Shell;

/// <summary>
/// Loads and persists the shell-access master switch (AC-1066) in <c>cockpit.json</c>. When nothing was ever saved,
/// <see cref="LoadAsync"/> returns <see cref="ShellAccessSettings.Default"/> (off) — the feature is opt-in.
/// </summary>
public interface IShellAccessSettingsStore
{
    Task<ShellAccessSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ShellAccessSettings settings, CancellationToken cancellationToken = default);
}
