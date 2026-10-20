using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Provisioning;

namespace Cockpit.Infrastructure.Terminal;

// The live terminal-access master switch (AC-34), read synchronously by the endpoint fan-out; seeded at startup and
// flipped live by the Options toggle. AC-1593: an organisation that forbids terminal access keeps it off whatever is
// switched, and the switch itself is kept.
internal sealed class TerminalAccessState(IOrgPolicy? orgPolicy = null) : ITerminalAccessSwitch, ISingletonService
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled || OrgPolicyCeilings.TerminalForbidden(orgPolicy?.Current) && _enabled;
        set => _enabled = value;
    }
}
