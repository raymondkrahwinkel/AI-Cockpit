using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Shell;

namespace Cockpit.Infrastructure.Shell;

// The live value of the shell-access master switch (AC-1066), read synchronously by the endpoint fan-out to decide
// whether `cockpit-shell` is advertised at all. Off by default; mirrors TerminalAccessState — seeded from
// ShellAccessSettings at startup, flipped live by the Options toggle.
internal sealed class ShellAccessState : IShellAccessSwitch, ISingletonService
{
    public bool Enabled { get; set; }
}
