using System.Collections.ObjectModel;

namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Claims some or all of one project's own host fields as externally managed (AC-604, route B) — the seam a
/// plugin that shares project definitions elsewhere uses to tell the project editor a field's true home is not
/// <c>cockpit.json</c>. Registered through <see cref="ICockpitHost.ClaimProjectOwnership"/>.
/// </summary>
/// <remarks>
/// <see cref="Default"/> is what every <see cref="HostProjectField"/> gets unless <see cref="Overrides"/> says
/// otherwise for it. A field's own override can itself be null, putting that field back to local despite a
/// non-null default.
/// </remarks>
/// <param name="ProjectId">
/// The project this claims, matched by <c>Project.Id</c>.
/// </param>
/// <param name="Default">
/// What every field gets unless <see cref="Overrides"/> names it. Null claims nothing by default.
/// </param>
public sealed record ProjectOwnershipRegistration(string ProjectId, ProjectFieldOwnership? Default = null)
{
    /// <summary>
    /// Per-field deviation from <see cref="Default"/> — a different ownership, or null to leave this field local.
    /// </summary>
    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?> Overrides { get; init; } =
        ReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>.Empty;
}
