using Cockpit.Core.Projects;

namespace Cockpit.Infrastructure.Projects;

/// <summary>
/// Checks a project's <see cref="ProjectResource"/> rows for a reference that names an absolute, existing-or-not
/// filesystem path and finds it missing (AC-484) — the one piece of I/O
/// <see cref="Cockpit.Core.Sessions.SessionStartDefaults.Resolve"/> deliberately never does itself (see that
/// method's own remarks on <c>unresolvedReferences</c>: purity is a property of that class, not an oversight here).
/// The layer that assembles an actual launch (<c>ProjectQuickStart</c>, the New-session dialog's Start) runs this
/// once and hands the result in as plain data.
/// <para>
/// Scope is deliberately narrow — only a reference that is a <em>fully qualified</em> path
/// (<see cref="Path.IsPathFullyQualified(string)"/>) is checked at all:
/// </para>
/// <list type="bullet">
/// <item><description>
/// A <c>&lt;scheme&gt;:&lt;value&gt;</c> reference (<see cref="ProjectMemoryRef.TryParse"/>) is never checked —
/// only the plugin that registered that scheme could judge whether its value is reachable, and this probe knows
/// nothing about plugins.
/// </description></item>
/// <item><description>
/// A relative path is never checked either — whether a relative path travels with the project it is relative to is
/// AC-485's question, not this one's.
/// </description></item>
/// </list>
/// <para>
/// Better to say nothing about a reference this probe cannot fairly judge than to call it broken when it might
/// simply be a kind — a scheme, a relative path — outside what a filesystem check can answer.
/// </para>
/// </summary>
public static class ProjectResourceProbe
{
    /// <summary>
    /// The <see cref="ProjectResource.Reference"/> value of every row in <paramref name="resources"/> that is a
    /// fully qualified path and does not exist as either a file or a directory. Never throws: a probe is a
    /// convenience, not a dependency (the same line the bundled-plugin installer draws), so a reference this
    /// runtime cannot even parse as a path is treated the same as one this probe was never asked about — left out
    /// of the result rather than reported broken.
    /// </summary>
    public static IReadOnlyCollection<string> FindUnresolved(IEnumerable<ProjectResource> resources)
    {
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in resources.Select(resource => resource.Reference).Distinct(StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(reference))
            {
                continue;
            }

            try
            {
                // A <scheme>:<value> reference is the plugin's to judge, not this probe's — see the class remarks.
                if (ProjectMemoryRef.TryParse(reference, out _, out _))
                {
                    continue;
                }

                // A relative path's portability is AC-485's concern; only an absolute, fully qualified path is
                // ever weighed in as "broken" here.
                if (!Path.IsPathFullyQualified(reference))
                {
                    continue;
                }

                if (!File.Exists(reference) && !Directory.Exists(reference))
                {
                    unresolved.Add(reference);
                }
            }
            catch
            {
                // A reference this runtime cannot even parse as a path (invalid characters, too long, …) is not
                // this probe's to call broken — better silent than wrongly accusing a value of the wrong kind.
            }
        }

        return unresolved;
    }
}
