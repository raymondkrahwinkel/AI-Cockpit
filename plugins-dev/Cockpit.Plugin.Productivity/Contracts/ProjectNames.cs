using System.Text.RegularExpressions;
using Cockpit.Plugins.Abstractions.Projects;
using Cockpit.Plugins.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.TimeTracking;

namespace Cockpit.Plugin.Productivity.Contracts;

// What one session in a range is (AC-1766): its project and ticket from the timeline, its name and profile from the
// cockpit while it is open, the profile and model from its usage. HasTimeline is false for a session that only has
// usage, so its active time is unknown rather than zero.
internal sealed record SessionLabel(
    string PaneId,
    string? ProjectId,
    string? Ticket,
    string? Name,
    string? Profile,
    string? Model,
    bool HasTimeline)
{
    public static IReadOnlyList<SessionLabel> From(CockpitTimeline timeline, IReadOnlyList<OpenCockpitSession> open)
    {
        var names = open.ToDictionary(session => session.PaneId, StringComparer.Ordinal);
        var lines = timeline.Lines.Where(line => line.PaneId is not null).ToLookup(line => line.PaneId ?? string.Empty, StringComparer.Ordinal);
        var days = timeline.Days.ToLookup(day => day.PaneId, StringComparer.Ordinal);
        var usage = timeline.Usage.ToLookup(value => value.PaneId, StringComparer.Ordinal);
        return [.. lines.Select(group => group.Key).Concat(days.Select(group => group.Key)).Concat(usage.Select(group => group.Key))
            .Distinct(StringComparer.Ordinal)
            .Select(pane =>
            {
                var session = names.GetValueOrDefault(pane);
                var ticket = lines[pane].LastOrDefault(line => line.Ticket is not null)?.Ticket
                    ?? days[pane].SelectMany(day => day.Tickets).LastOrDefault()
                    ?? _TicketIn(session?.Name);
                return new SessionLabel(
                    pane,
                    lines[pane].LastOrDefault(line => line.ProjectId is not null)?.ProjectId ?? days[pane].LastOrDefault(day => day.ProjectId is not null)?.ProjectId,
                    ticket,
                    session?.Name,
                    session?.ProfileLabel ?? usage[pane].LastOrDefault(value => value.ProfileLabel is not null)?.ProfileLabel,
                    usage[pane].Select(value => value.Model).Concat(days[pane].Select(day => day.Model)).LastOrDefault(model => string.IsNullOrWhiteSpace(model) is false),
                    lines[pane].Any() || days[pane].Any());
            })];
    }

    private static string? _TicketIn(string? name) =>
        name is null ? null : Regex.Match(name, @"\b[A-Z][A-Z0-9]+-\d+\b") is { Success: true } match ? match.Value : null;
}

internal sealed record ProjectName(string Label, string? Tooltip, string? ProjectId);

// Turns a stored project value into the name the operator knows (AC-1766): a project id, or — from before AC-1737 —
// the folder a session ran in. A worktree folder ends in its session's short id, and that session's timeline knows
// the project. A value that leads nowhere stays visible as unknown rather than as a bare hash.
internal sealed class ProjectNames(IReadOnlyList<PluginProjectInfo> projects, IReadOnlyList<SessionLabel> sessions)
{
    public const string NoProject = "geen project";

    public static ProjectNames Empty { get; } = new([], []);

    public ProjectName Resolve(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ProjectName(NoProject, null, null);
        }

        if (_Find(value) is { } project)
        {
            return new ProjectName(project.Name, null, project.Id);
        }

        var shown = value.Length > 8 ? value[..8] + "…" : value;
        return new ProjectName($"Onbekend project ({shown})", $"'{value}' hoort bij geen project in de cockpit: het project is weg, of de map is van vóór de projecten.", null);
    }

    // A session's line: project · ticket · name, leaving out what is not known.
    public string Describe(SessionLabel session, bool withProject = true)
    {
        var parts = withProject ? new List<string> { Resolve(session.ProjectId).Label } : [];
        if (session.Ticket is { } ticket)
        {
            parts.Add(ticket);
        }

        // A name that opens with its ticket ("AC-1764 sidebar") keeps only the rest beside the ticket.
        var name = session.Ticket is { } prefix && session.Name?.StartsWith(prefix, StringComparison.Ordinal) == true
            ? session.Name[prefix.Length..].TrimStart(' ', '·', '-', ':')
            : session.Name;
        if (string.IsNullOrWhiteSpace(name) is false)
        {
            parts.Add(name);
        }

        if (parts.Count == (withProject ? 1 : 0))
        {
            parts.Add($"sessie {session.PaneId[..Math.Min(8, session.PaneId.Length)]}");
        }

        return string.Join(" · ", parts);
    }

    private PluginProjectInfo? _Find(string value)
    {
        if (projects.FirstOrDefault(project => string.Equals(project.Id, value, StringComparison.Ordinal)) is { } byId)
        {
            return byId;
        }

        var folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(value.Trim()));
        var shortId = folder[(folder.LastIndexOf('-') + 1)..];
        if (shortId.Length == 8
            && sessions.Where(session => session.PaneId.StartsWith(shortId, StringComparison.OrdinalIgnoreCase))
                .Select(session => projects.FirstOrDefault(project => string.Equals(project.Id, session.ProjectId, StringComparison.Ordinal)))
                .FirstOrDefault(project => project is not null) is { } bySession)
        {
            return bySession;
        }

        return projects.FirstOrDefault(project => string.Equals(project.Name, folder, StringComparison.OrdinalIgnoreCase));
    }
}
