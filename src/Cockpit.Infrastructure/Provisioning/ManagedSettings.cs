using Cockpit.Core.Abstractions;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Provisioning;

internal sealed class ManagedSettings : IManagedSettings, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public ManagedSettings()
        : this(CockpitConfigPath.Default)
    {
    }

    internal ManagedSettings(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Provisioning?.Managed.ToDictionary(managed => managed.Key, managed => managed.Value.By)
        ?? new Dictionary<string, string>();
}
