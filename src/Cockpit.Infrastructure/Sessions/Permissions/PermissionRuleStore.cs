using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Sessions.Permissions;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Sessions.Permissions;

// Persists always-allow rules under `permissionRules` in `cockpit.json`, keyed by profile label
// (same file/pattern as `SessionProfileStore`/`NotificationSettingsStore`). Read-modify-write via
// `CockpitConfigFileAccess` leaves other sections and sibling profiles' rules untouched.
internal sealed class PermissionRuleStore : IPermissionRuleStore, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public PermissionRuleStore()
        : this(CockpitConfigPath.Default)
    {
    }

    // Test seam: point the store at an arbitrary config file path.
    internal PermissionRuleStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<PermissionRule>> LoadAsync(string? profileLabel, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(profileLabel))
        {
            return [];
        }

        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null || !configFile.PermissionRules.TryGetValue(profileLabel, out var entries))
        {
            return [];
        }

        return entries.Select(entry => entry.ToDomain()).ToList();
    }

    public Task AddAsync(string? profileLabel, PermissionRule rule, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(profileLabel))
        {
            return Task.CompletedTask;
        }

        return _configFile.UpdateAsync(
            file =>
            {
                if (!file.PermissionRules.TryGetValue(profileLabel, out var entries))
                {
                    entries = [];
                    file.PermissionRules[profileLabel] = entries;
                }

                var candidate = PermissionRuleEntry.FromDomain(rule);
                var alreadyPresent = entries.Any(existing =>
                    existing.ToolName == candidate.ToolName
                    && existing.Scope == candidate.Scope
                    && existing.InputMatch == candidate.InputMatch);

                if (!alreadyPresent)
                {
                    entries.Add(candidate);
                }
            },
            cancellationToken);
    }
}
