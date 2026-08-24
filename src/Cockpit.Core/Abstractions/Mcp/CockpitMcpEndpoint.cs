namespace Cockpit.Core.Abstractions.Mcp;

// AC-1013: Register one of these (#AC-13, #AC-12) to add a cockpit-hosted MCP server, in-process/DI-resolved
// and answered live rather than written to the registry (AC-40). IsEnabled is a live master switch (AC-34);
// Internal (AC-204) hides it from user selection but allows explicit mounting; AlwaysMounted also hides it but forces it into every session regardless of selection.
public sealed record CockpitMcpEndpoint(
    string ServerName,
    Type ToolsType,
    Func<bool>? IsEnabled = null,
    bool Internal = false,
    bool AlwaysMounted = false);
