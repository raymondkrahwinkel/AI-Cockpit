using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;
using Cockpit.Core.Provisioning;

namespace Cockpit.Infrastructure.Shell;

// The live value of the shell-access master switch (AC-1066), read synchronously by the endpoint fan-out to decide
// whether `cockpit-shell` is advertised at all. Off by default; mirrors TerminalAccessState — seeded from
// ShellAccessSettings at startup, flipped live by the Options toggle.
// AC-1593: an organisation that forbids shell access keeps it off whatever is switched; the switch itself is kept.
internal sealed class ShellAccessState(IOrgPolicy? orgPolicy = null) : IShellAccessSwitch, ISingletonService
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled && !OrgPolicyCeilings.ShellForbidden(orgPolicy?.Current);
        set => _enabled = value;
    }
}
