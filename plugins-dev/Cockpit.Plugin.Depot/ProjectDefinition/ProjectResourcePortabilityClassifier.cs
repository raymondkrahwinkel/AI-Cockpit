namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// Classifies a resource reference into one of the four <see cref="ProjectResourcePortability"/> shapes. Only
/// <see cref="ProjectResourcePortability.RepoRelative"/> and <see cref="ProjectResourcePortability.PluginSource"/> travel with a shared <c>.cockpit/project.json</c> (AC-244 decision, 2026-08-02).
/// </summary>
// AC-244 finding (2026-08-02, measured against Cockpit.Core.Projects.ProjectResourcePathPortability.IsMachineBound):
// it disagrees with this classifier, silently, on "~/x" and on an absolute-but-inside-SourceDirectory reference
// that bypassed ToStoredReference — the editor shows no warning for either, this classifier drops both. Not fixed
// here (host-side, different ticket) — see CockpitProjectResourceFilter for the drop report this backs instead.
public static class ProjectResourcePortabilityClassifier
{
    public static ProjectResourcePortability Classify(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (_HasPluginScheme(reference))
        {
            return ProjectResourcePortability.PluginSource;
        }

        if (reference.StartsWith('~'))
        {
            return ProjectResourcePortability.AnchorRelative;
        }

        return Path.IsPathFullyQualified(reference) ? ProjectResourcePortability.Absolute : ProjectResourcePortability.RepoRelative;
    }

    public static bool IsPortable(ProjectResourcePortability portability) =>
        portability is ProjectResourcePortability.RepoRelative or ProjectResourcePortability.PluginSource;

    public static string ToWireValue(ProjectResourcePortability portability) => portability switch
    {
        ProjectResourcePortability.RepoRelative => "repo-relative",
        ProjectResourcePortability.AnchorRelative => "anchor-relative",
        ProjectResourcePortability.PluginSource => "plugin-source",
        ProjectResourcePortability.Absolute => "absolute",
        _ => throw new ArgumentOutOfRangeException(nameof(portability), portability, null),
    };

    // Mirrors Cockpit.Core.Projects.ProjectMemoryRef.TryParse's own floor: a Windows path's drive letter
    // ("C:\Users\raymond") puts a colon at index 1 too, with "C" in front of it — without a two-character floor a
    // path would misparse as a reference to a one-character scheme "c" instead of the folder it plainly is.
    private static bool _HasPluginScheme(string reference)
    {
        var separator = reference.IndexOf(':');
        return separator >= 2 && reference[(separator + 1)..].Trim().Length > 0;
    }
}
