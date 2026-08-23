namespace Cockpit.Plugins.Abstractions.Sessions;

/// <summary>
/// One tool a host-run tool loop can call, described in provider-neutral terms (AC-964).
/// The schema travels as JSON text so this assembly needs no <c>Microsoft.Extensions.AI</c> reference.
/// </summary>
/// <param name="ServerName">
/// The MCP server the tool came from, e.g. <c>cockpit-session</c>.
/// </param>
/// <param name="Name">
/// The tool's name, as the model must call it.
/// </param>
/// <param name="Description">
/// What the tool does, for the model to choose by; may be empty.
/// </param>
/// <param name="InputSchemaJson">
/// The tool's JSON Schema for its arguments, as text.
/// </param>
public sealed record PluginToolDescriptor(
    string ServerName,
    string Name,
    string? Description,
    string InputSchemaJson);
