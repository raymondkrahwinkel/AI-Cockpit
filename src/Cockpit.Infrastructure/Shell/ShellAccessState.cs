using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Provisioning;

namespace Cockpit.Infrastructure.Shell;

// The live shell-access master switch (AC-1066), read synchronously to decide whether `cockpit-shell` is advertised;
// off by default, seeded from ShellAccessSettings and flipped live by Options. AC-1593: an organisation that forbids
// shell access keeps it off whatever is switched, and the switch itself is kept.
internal sealed class ShellAccessState(IOrgPolicy? orgPolicy = null) : IShellAccessSwitch, ISingletonService
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled || OrgPolicyCeilings.ShellForbidden(orgPolicy?.Current) && _enabled;
        set => _enabled = value;
    }
}
