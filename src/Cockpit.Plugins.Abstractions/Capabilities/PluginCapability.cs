namespace Cockpit.Plugins.Abstractions.Capabilities;

/// <summary>
/// One named thing a plugin can ask the host for, as listed in <see cref="CapabilityCatalog"/>. The unit a
/// manifest declares, a dialog shows and a grant is filed under.
/// </summary>
/// <remarks>
/// A capability is a group of contribution points that are one decision to the operator, not one member each:
/// a grant dialog that listed ninety methods would be read by nobody.
/// </remarks>
/// <param name="Id">
/// A stable, dotted id (e.g. <c>mcp.call</c>). Persisted in manifests and grants, so treat it as API surface.
/// </param>
/// <param name="Title">
/// The capability in the operator's words, as a grant dialog would head it.
/// </param>
/// <param name="Summary">
/// One line saying what saying yes hands over. Written for the operator, not for the plugin author.
/// </param>
/// <param name="Risk">
/// What granting it costs — see <see cref="CapabilityRisk"/>.
/// </param>
/// <param name="SinceHostVersion">
/// The host version this capability first existed in, from the release history in <c>Directory.Build.props</c>.
/// The capability's own age, not each member's: an individual member added later still sets its own
/// <c>minHostVersion</c>, which stays that file's job.
/// </param>
/// <param name="ContributionPoints">
/// The SDK members this capability covers, as <c>Interface.Member</c> — overloads share one entry, since an
/// overload of a covered member is not a new thing to ask for.
/// </param>
/// <param name="Scope">
/// The dimensions a grant can be narrowed along, empty when the capability is all-or-nothing.
/// </param>
public sealed record PluginCapability(
    string Id,
    string Title,
    string Summary,
    CapabilityRisk Risk,
    string SinceHostVersion,
    IReadOnlyList<string> ContributionPoints,
    IReadOnlyList<CapabilityScopeField> Scope);
