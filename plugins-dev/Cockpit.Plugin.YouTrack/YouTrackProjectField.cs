using Cockpit.Plugins.Abstractions.Projects;

namespace Cockpit.Plugin.YouTrack;

/// <summary>
/// The field this plugin puts on a cockpit project (AC-317): which YouTrack project it is tracked in. The stored
/// value is the short name — <c>AC</c> — because that is what every query this plugin makes is written in; the
/// operator picks it by the full name, which is the only half they know by heart.
/// </summary>
internal static class YouTrackProjectField
{
    /// <summary>What the link is stored under on the project. Never change it: already-linked projects are keyed by it.</summary>
    public const string Key = "youtrack.project";

    public static ProjectFieldRegistration Registration(YouTrackSettings settings, YouTrackClient client) =>
        new(
            Key,
            "YouTrack project",
            cancellationToken => BuildOptionsAsync(
                settings.Instances,
                (instance, token) => client.GetProjectsAsync(instance.InstanceUrl, instance.Token, token),
                cancellationToken))
        {
            Hint = "Which project in YouTrack this one is tracked in. The issues dialog then opens on it instead of on everything.",
            Placeholder = "AC",
        };

    /// <summary>
    /// Every project on every configured instance. Prefixed with the instance label only when there is more than one
    /// configured — the prefix answers "which YouTrack", a question a single-instance cockpit never asks.
    /// <para>
    /// Takes the fetch as an argument rather than the client, so what this decides — which instances count, how a
    /// choice reads, what an empty answer means — is testable without a YouTrack to answer.
    /// </para>
    /// </summary>
    internal static async Task<IReadOnlyList<ProjectFieldOption>> BuildOptionsAsync(
        IReadOnlyList<YouTrackInstance> instances,
        Func<YouTrackInstance, CancellationToken, Task<IReadOnlyList<YouTrackProject>>> loadProjects,
        CancellationToken cancellationToken)
    {
        var configured = instances
            .Where(instance => !string.IsNullOrWhiteSpace(instance.InstanceUrl) && !string.IsNullOrWhiteSpace(instance.Token))
            .ToList();

        if (configured.Count == 0)
        {
            return [];
        }

        var options = new List<ProjectFieldOption>();
        foreach (var instance in configured)
        {
            foreach (var project in await loadProjects(instance, cancellationToken))
            {
                var name = string.IsNullOrWhiteSpace(project.Name) ? project.ShortName : $"{project.Name} — {project.ShortName}";
                options.Add(new ProjectFieldOption(
                    project.ShortName,
                    configured.Count > 1 ? $"{instance.Label}: {name}" : name));
            }
        }

        // GetProjectsAsync answers an unreachable instance or a token without admin read the same way it answers an
        // empty one — with nothing. Told apart here rather than there, because the issues dialog wants the silent
        // fallback and this field must not: an empty list under a configured instance reads as "you have no
        // projects", which is the one thing it almost never means.
        if (options.Count == 0)
        {
            throw new InvalidOperationException(
                "No projects came back. The instance may be unreachable, or its token may not be allowed to read the project list.");
        }

        // Two instances can host a project with the same short name, and the link stores only that name — so the
        // second one would be a choice that saves as the first. Kept once rather than offered twice.
        return [.. options
            .DistinctBy(option => option.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(option => option.Display, StringComparer.OrdinalIgnoreCase)];
    }
}
