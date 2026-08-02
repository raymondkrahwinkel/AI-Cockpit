using System.Collections.ObjectModel;

namespace Cockpit.Plugins.Abstractions.Projects;

/// <summary>
/// Claims some or all of one project's own host fields as externally managed (AC-604, route B) — the seam a
/// plugin that shares project definitions elsewhere (AC-242's Depot sync) uses to tell the project editor a
/// field's true home is not <c>cockpit.json</c>. Registered through
/// <see cref="ICockpitHost.ClaimProjectOwnership"/>, next to <see cref="ProjectFieldRegistration"/>, which adds
/// a field of a plugin's own rather than claiming one that already exists.
/// <para>
/// <see cref="Default"/> is what every <see cref="HostProjectField"/> gets unless <see cref="Overrides"/> says
/// otherwise for it — null claims nothing by default, so a plugin sharing only a couple of fields sets those two
/// in <see cref="Overrides"/> and leaves <see cref="Default"/> null rather than opting the other four out one at
/// a time. A field's own override can itself be null, which puts that field back to local despite a non-null
/// default — the mixed case (name and behaviour shared, folder and profile local) a whole-project claim alone
/// cannot express.
/// </para>
/// </summary>
/// <param name="ProjectId">The project this claims, matched by <c>Project.Id</c>.</param>
/// <param name="Default">What every field gets unless <see cref="Overrides"/> names it. Null claims nothing by default.</param>
public sealed record ProjectOwnershipRegistration(string ProjectId, ProjectFieldOwnership? Default = null)
{
    /// <summary>Per-field deviation from <see cref="Default"/> — a different ownership, or null to leave this field local.</summary>
    public IReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?> Overrides { get; init; } =
        ReadOnlyDictionary<HostProjectField, ProjectFieldOwnership?>.Empty;
}
