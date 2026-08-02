using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Worktrees;
using Cockpit.Core.Worktrees;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Worktrees;

// Persists `WorktreeSettings` under the `worktreeSettings` section of `cockpit.json`, going
// through `CockpitConfigFileAccess` so it leaves the other sections — including the worktree registry —
// untouched (same pattern as the terminal/layout settings stores).
internal sealed class WorktreeSettingsStore : IWorktreeSettingsStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public WorktreeSettingsStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal WorktreeSettingsStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public string DefaultRoot => CockpitConfigPath.WorktreesRoot;

    public async Task<WorktreeSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        return configFile?.WorktreeSettings?.ToDomain() ?? new WorktreeSettings();
    }

    public Task SaveAsync(WorktreeSettings settings, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.WorktreeSettings = WorktreeSettingsEntry.FromDomain(settings),
            cancellationToken);
}
