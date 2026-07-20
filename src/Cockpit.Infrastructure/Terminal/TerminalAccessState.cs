using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Terminal;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The live value of the terminal-access master switch (AC-34), read synchronously by the endpoint fan-out to decide
/// whether the <c>cockpit-terminal</c> server is advertised to a session at all. Off by default: the feature is opt-in.
/// <para>
/// A tiny mutable singleton rather than an async settings read, because the fan-out asks "is it on?" on a hot path and
/// wants an immediate answer. Its initial value is seeded from the persisted <see cref="Core.Terminal.TerminalAccessSettings"/>
/// at startup, and the Options toggle flips it live (and persists), so a change takes effect on the next session
/// without a restart.
/// </para>
/// </summary>
internal sealed class TerminalAccessState : ITerminalAccessSwitch, ISingletonService
{
    public bool Enabled { get; set; }
}
