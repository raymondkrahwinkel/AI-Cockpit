namespace Cockpit.Plugin.Depot.ProjectDefinition;

/// <summary>
/// Classifies a resource reference into one of the four <see cref="ProjectResourcePortability"/> shapes. Three of
/// the four — <see cref="ProjectResourcePortability.RepoRelative"/>, <see cref="ProjectResourcePortability.AnchorRelative"/>
/// and <see cref="ProjectResourcePortability.PluginSource"/> — travel with a shared <c>.cockpit/project.json</c>
/// (AC-605 decision, 2026-08-02, reversing AC-244's original two-of-four call on <c>AnchorRelative</c> — see
/// <see cref="IsPortable"/>'s own remark).
/// </summary>
// AC-605 (2026-08-02, measured against Cockpit.Core.Projects.ProjectResourcePathPortability.ClassifyScope): the two
// now agree on every shape, including "~/x" — see ProjectResourceScopeParityTests for the shared table both sides
// are pinned against. An absolute-but-inside-SourceDirectory reference that bypassed ToStoredReference is still
// this classifier's to drop (it has no SourceDirectory to judge that against, and never will — see this plugin's
// own "cannot reference Cockpit.Core" constraint); the host's own SuggestRepoRelativeFix is what surfaces and offers
// to repair that case instead (AC-605 criterion 5) before a project ever gets to this classifier at all.
public static class ProjectResourcePortabilityClassifier
{
    public static ProjectResourcePortability Classify(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (_HasPluginScheme(reference))
        {
            return ProjectResourcePortability.PluginSource;
        }

        if (_IsHomeAnchored(reference))
        {
            return ProjectResourcePortability.AnchorRelative;
        }

        return Path.IsPathFullyQualified(reference) ? ProjectResourcePortability.Absolute : ProjectResourcePortability.RepoRelative;
    }

    public static bool IsPortable(ProjectResourcePortability portability) =>
        portability is ProjectResourcePortability.RepoRelative
            or ProjectResourcePortability.AnchorRelative
            or ProjectResourcePortability.PluginSource;

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

    // Mirrors Cockpit.Core.Projects.ProjectResourcePathPortability.IsHomeAnchored exactly (AC-605 criterion 4) —
    // this plugin cannot reference Cockpit.Core (see this class's own remarks), so the rule is reimplemented rather
    // than shared, and ProjectResourceScopeParityTests is what keeps the two copies from drifting apart again.
    // Deliberately narrower than a bare reference.StartsWith('~'): "~henk/x" is a POSIX shell's "some other user's
    // home" expansion, not a form either side resolves — only "~" itself and anything starting with "~/" count.
    private static bool _IsHomeAnchored(string reference) =>
        reference == "~" || reference.StartsWith("~/", StringComparison.Ordinal);
}
