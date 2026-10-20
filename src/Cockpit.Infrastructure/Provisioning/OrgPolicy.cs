using Cockpit.Core.Abstractions;
using Cockpit.Core.Provisioning;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Provisioning;

internal sealed class OrgPolicy : IOrgPolicy, ISingletonService
{
    // What an unreadable policy means to a synchronous reader: the agent switches off, never an exception in the fan-out.
    private static readonly ProvisionPolicy Unreadable = new(
        "an organisation policy that could not be read",
        Permissions: new ProvisionPermissionPolicy(TerminalAccess: OrgPolicyCeilings.Forbidden, ShellAccess: OrgPolicyCeilings.Forbidden));

    private readonly CockpitConfigFileAccess _configFile;
    private readonly ILogger<OrgPolicy>? _logger;
    private ProvisionPolicy? _current;
    private volatile bool _stale = true;

    public OrgPolicy(ILogger<OrgPolicy>? logger = null)
        : this(CockpitConfigPath.Default, logger)
    {
    }

    internal OrgPolicy(string configFilePath, ILogger<OrgPolicy>? logger = null)
    {
        _logger = logger;
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
                // Cleared before the read, so a save during it marks it stale again; a failed read stays stale and fails closed.
                _stale = false;
                try
                {
                    _current = ReadAsync().GetAwaiter().GetResult();
                }
                catch (Exception exception)
                {
                    _stale = true;
                    _logger?.LogWarning(exception, "The organisation policy could not be read; terminal and shell access stay off until it can");
                    return null;
                }
            }

            return _current;
        }
    }

    public async Task<ProvisionPolicy?> ReadAsync(CancellationToken cancellationToken = default) =>
        (await _configFile.ReadAsync(cancellationToken).ConfigureAwait(false))?.Provisioning?.Policy;
}
