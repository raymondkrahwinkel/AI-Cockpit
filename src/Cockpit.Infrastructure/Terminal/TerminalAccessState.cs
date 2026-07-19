using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Terminal;

/// <summary>
/// The live value of the terminal-access master switch (AC-34), read synchronously by the endpoint fan-out to decide
/// whether the <c>cockpit-terminal</c> server is advertised to a session at all. Off by default: the feature is opt-in.
/// <para>
/// A tiny mutable singleton rather than an async settings read, because the fan-out asks "is it on?" on a hot path and
/// wants an immediate answer. Phase 2 loads its initial value from the persisted <see cref="Core.Terminal.TerminalAccessSettings"/>
/// at startup and updates it when the Options toggle saves; until then it stays at its safe default (off).
/// </para>
/// </summary>
internal sealed class TerminalAccessState : ISingletonService
{
    public bool Enabled { get; set; }
}
