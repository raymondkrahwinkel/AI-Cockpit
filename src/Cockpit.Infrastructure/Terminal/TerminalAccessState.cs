using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;
using Cockpit.Core.Provisioning;

namespace Cockpit.Infrastructure.Terminal;

// The live value of the terminal-access master switch (AC-34), read synchronously by the endpoint fan-out.
// A tiny mutable singleton rather than an async settings read, since the fan-out needs an immediate answer.
// Seeded from persisted settings at startup; the Options toggle flips it live, taking effect next session.
// AC-1593: an organisation that forbids terminal access keeps it off whatever is switched; the switch itself is kept.
internal sealed class TerminalAccessState(IOrgPolicy? orgPolicy = null) : ITerminalAccessSwitch, ISingletonService
{
    private bool _enabled;

    public bool Enabled
    {
        get => _enabled && !OrgPolicyCeilings.TerminalForbidden(orgPolicy?.Current);
        set => _enabled = value;
    }
}
