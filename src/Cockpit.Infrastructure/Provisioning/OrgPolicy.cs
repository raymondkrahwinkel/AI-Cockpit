using Cockpit.Core.Abstractions;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Provisioning;

internal sealed class OrgPolicy : IOrgPolicy, ISingletonService
{
    private readonly CockpitConfigFileAccess _configFile;
    private ProvisionPolicy? _current;
    private volatile bool _stale = true;

    public OrgPolicy()
        : this(CockpitConfigPath.Default)
    {
    }

    internal OrgPolicy(string configFilePath)
    {
        _configFile = new CockpitConfigFileAccess(configFilePath);
        CockpitConfigFileAccess.Written += path =>
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(configFilePath), StringComparison.OrdinalIgnoreCase))
            {
                _stale = true;
            }
        };
    }

    // Read on first use and after a save; ReadAsync awaits with ConfigureAwait(false) throughout, so waiting here holds no context.
    public ProvisionPolicy? Current
    {
        get
        {
            if (_stale)
            {
                _stale = false;
                _current = ReadAsync().GetAwaiter().GetResult();
            }

            return _current;
        }
    }

    public async Task<ProvisionPolicy?> ReadAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Provisioning?.Policy;
}
