using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;

namespace Cockpit.Infrastructure.Terminal;

// The live value of the terminal-access master switch (AC-34), read synchronously by the endpoint fan-out.
// A tiny mutable singleton rather than an async settings read, since the fan-out needs an immediate answer.
// Seeded from persisted settings at startup; the Options toggle flips it live, taking effect next session.
internal sealed class TerminalAccessState : ITerminalAccessSwitch, ISingletonService
{
    public bool Enabled { get; set; }
}
