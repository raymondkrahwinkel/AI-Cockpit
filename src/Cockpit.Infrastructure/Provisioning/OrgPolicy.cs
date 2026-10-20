using Cockpit.Core.Abstractions;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Provisioning;

internal sealed class OrgPolicy : IOrgPolicy, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;

    public OrgPolicy()
        : this(CockpitConfigPath.Default)
    {
    }

    internal OrgPolicy(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
    }

    public async Task<ProvisionPolicy?> ReadAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Provisioning?.Policy;
}
