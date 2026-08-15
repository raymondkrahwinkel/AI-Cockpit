namespace Cockpit.Core.Mcp;

// The network-node master switch (AC-790): off by default, so a Cockpit nobody meant as a node stays on
// loopback only. Turning it on adds a second, HTTPS listener next to each mounted endpoint's existing loopback
// one, guarded by SharedSecret instead of this run's ephemeral McpAuthKey — the credential a second Cockpit
// types in by hand when it adds this node as a plain HTTP MCP server (AC-354), so it has to survive a restart.
// Takes effect on the next launch, same as RenderingSettingsEntry — no dynamic Kestrel listener reconfiguration.
public sealed record NodeEndpointSettings
{
    public bool Enabled { get; init; }
    public string SharedSecret { get; init; } = "";

    public static NodeEndpointSettings Default { get; } = new();
}
