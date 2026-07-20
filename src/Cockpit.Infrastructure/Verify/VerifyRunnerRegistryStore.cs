using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Verify;
using Cockpit.Core.Verify;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Verify;

/// <summary>
/// Persists the verify-runner registry under the <c>verifyRunners</c> section of <c>cockpit.json</c>, going through
/// <see cref="CockpitConfigFileAccess"/> so each mutation is a gated read-modify-write that never clobbers a sibling
/// section — the same seam the worktree registry and the profile store use.
/// </summary>
internal sealed class VerifyRunnerRegistryStore : IVerifyRunnerRegistry, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public VerifyRunnerRegistryStore()
        : this(CockpitConfigPath.Default)
    {
    }

    /// <summary>Test seam: point the registry at an arbitrary config file path.</summary>
    internal VerifyRunnerRegistryStore(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyList<VerifyRunner>> ListAsync(CancellationToken cancellationToken = default)
    {
        var configFile = await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (configFile is null)
        {
            return [];
        }

        return configFile.VerifyRunners.Select(entry => entry.ToDomain()).ToList();
    }

    public Task SaveAsync(VerifyRunner runner, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file =>
            {
                file.VerifyRunners.RemoveAll(entry => _SameLabel(entry.Label, runner.Label));
                file.VerifyRunners.Add(VerifyRunnerEntry.FromDomain(runner));
            },
            cancellationToken);

    public Task RemoveAsync(string label, CancellationToken cancellationToken = default) =>
        _configFile.UpdateAsync(
            file => file.VerifyRunners.RemoveAll(entry => _SameLabel(entry.Label, label)),
            cancellationToken);

    private static bool _SameLabel(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
