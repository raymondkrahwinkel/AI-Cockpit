namespace Cockpit.Plugins.Abstractions.Capabilities;

/// <summary>
/// One dimension a grant of a capability can be narrowed along — the MCP server it may call, the profile
/// it may start. A capability whose scope schema is empty is all-or-nothing.
/// </summary>
/// <remarks>
/// The values themselves are not here: what a plugin asks for lives in its manifest and what was granted
/// lives with the grant. This only names the keys those two have to agree on.
/// </remarks>
/// <param name="Key">
/// The key a manifest and a grant both use, matching the parameter it narrows (e.g. <c>serverName</c>).
/// </param>
/// <param name="Description">
/// What a value means, written for whoever reads the grant rather than for the compiler.
/// </param>
public sealed record CapabilityScopeField(string Key, string Description);
