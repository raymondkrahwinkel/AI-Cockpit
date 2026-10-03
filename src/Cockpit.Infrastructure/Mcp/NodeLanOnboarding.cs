using Cockpit.Core.Abstractions;

namespace Cockpit.Infrastructure.Mcp;

// AC-1444: whether the node also answers on the LAN, through discovery and pairing. On, as on the desktop; a server
// takes connect keys only (AC-1355) and turns it off before `Start`.
public sealed class NodeLanOnboarding : ISingletonService
{
    public bool Enabled { get; set; } = true;
}
