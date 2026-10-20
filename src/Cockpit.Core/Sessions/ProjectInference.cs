using System.Text.RegularExpressions;

namespace Cockpit.Core.Sessions;

// What the host knows of a project and a live session when it files a prompt (assistant threads).
public sealed record ProjectHint(string Id, string Name, IReadOnlyList<string> TicketPrefixes);

public sealed record SessionHint(string PaneId, string Title, string? ProjectId);

// One project a prompt names, how the host knows, and where the prompt first names it.
public sealed record InferredProject(string ProjectId, ProjectSource Source, int At);

// Files a prompt the operator did not put anywhere (typed in All, or spoken) by what it names, before the turn runs:
// the assistant's own set_thread is a tool call it can skip, this is not. In the order the prompt names them, since
// that is the order its tags read in.
public static partial class ProjectInference
{
    // The brand word of a longer name ("Keuzehub" of "Keuzehub Voorlichting") is enough when no other project shares
    // it; shorter than this it is too likely an ordinary word.
    private const int MinDistinctiveWordLength = 5;

    // A session title is only a mention when it is long enough not to occur in ordinary text by chance.
    private const int MinSessionTitleLength = 8;

    public static IReadOnlyList<InferredProject> Infer(
        string text, IReadOnlyList<ProjectHint> projects, IReadOnlyList<SessionHint> sessions)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var found = new List<(int At, string ProjectId, ProjectSource Source)>();
        var known = projects.Select(project => project.Id).ToHashSet(StringComparer.Ordinal);

        // A session by its pane id or its full title: it is that session's project, and the session is the source.
        foreach (var session in sessions.Where(session => session.ProjectId is { } id && known.Contains(id)))
        {
            var at = text.IndexOf(session.PaneId, StringComparison.OrdinalIgnoreCase);
            if (at < 0 && session.Title.Trim().Length >= MinSessionTitleLength)
            {
                at = text.IndexOf(session.Title.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            if (at >= 0)
            {
                found.Add((at, session.ProjectId!, ProjectSource.Session));
            }
        }

        foreach (var project in projects)
        {
            // A ticket key ("AC-1594", spoken "ac 1594"); when a live session carries that key, the session is the source.
            foreach (var prefix in project.TicketPrefixes.Where(prefix => prefix.Length > 0))
            {
                foreach (Match key in Regex.Matches(text, $@"\b{Regex.Escape(prefix)}[-\s]?(\d+)\b", RegexOptions.IgnoreCase))
                {
                    var ticket = $"{prefix}-{key.Groups[1].Value}";
                    var bySession = sessions.Any(session => session.ProjectId == project.Id
                        && session.Title.Contains(ticket, StringComparison.OrdinalIgnoreCase));
                    found.Add((key.Index, project.Id, bySession ? ProjectSource.Session : ProjectSource.TicketPrefix));
                }

                // The prefix on its own, as a word in capitals ("open op VOV"), only where it is too long to be chance.
                if (prefix.Length >= 3 && Regex.Match(text, $@"\b{Regex.Escape(prefix)}\b") is { Success: true } bare)
                {
                    found.Add((bare.Index, project.Id, ProjectSource.TicketPrefix));
                }
            }

            if (_NameAt(text, project, projects) is var at and >= 0)
            {
                found.Add((at, project.Id, ProjectSource.Mentioned));
            }
        }

        // Where a project is named twice, the stronger source wins: a session over a ticket key over a name.
        return [.. found
            .GroupBy(hit => hit.ProjectId)
            .Select(group => (At: group.Min(hit => hit.At), ProjectId: group.Key, Source: group.Min(hit => _Rank(hit.Source)) switch
            {
                0 => ProjectSource.Session,
                1 => ProjectSource.TicketPrefix,
                _ => ProjectSource.Mentioned,
            }))
            .OrderBy(hit => hit.At)
            .Select(hit => new InferredProject(hit.ProjectId, hit.Source, hit.At))];
    }

    // A prompt naming two or more projects, split into each one's part: the clause each one is named in. A part starts
    // after the joiner before its mention (the first from the start when there is none) and ends where the next part's
    // joiner begins, so "en" or a comma belongs to neither.
    public static IReadOnlyList<TranscriptProjectPart> Parts(string text, IReadOnlyList<InferredProject> named)
    {
        if (named.Count < 2)
        {
            return [];
        }

        var clauses = named.Select((hit, i) => _Clause(text, hit.At) ?? (i == 0 ? (0, 0) : (hit.At, hit.At))).ToList();
        var parts = new List<TranscriptProjectPart>();
        for (var i = 0; i < named.Count; i++)
        {
            var start = clauses[i].Start;
            var end = i + 1 < clauses.Count ? clauses[i + 1].JoinerAt : text.Length;
            if (end > start)
            {
                parts.Add(new TranscriptProjectPart(named[i].ProjectId, start, end - start));
            }
        }

        return parts;
    }

    // ponytail: a handful of Dutch and English joiners; a parser of clauses is not worth it for where a tag starts.
    private static readonly string[] ClauseJoiners = [", ", "; ", ". ", " en ", " and ", " maar ", " but "];

    private static (int JoinerAt, int Start)? _Clause(string text, int mention)
    {
        (int JoinerAt, int Start)? best = null;
        foreach (var joiner in ClauseJoiners)
        {
            var at = mention == 0 ? -1 : text.LastIndexOf(joiner, mention - 1, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && at + joiner.Length <= mention && (best is null || at + joiner.Length > best.Value.Start))
            {
                best = (at, at + joiner.Length);
            }
        }

        return best;
    }

    private static int _Rank(ProjectSource source) => source switch
    {
        ProjectSource.Session => 0,
        ProjectSource.TicketPrefix => 1,
        _ => 2,
    };

    // The full name as words, or the first word of a longer name when it is distinctive enough and no other project's
    // name has it. ponytail: no fuzzy match, so a misspelt brand word is missed; add aliases to Project if that bites.
    private static int _NameAt(string text, ProjectHint project, IReadOnlyList<ProjectHint> projects)
    {
        var name = project.Name.Trim();
        if (name.Length == 0)
        {
            return -1;
        }

        if (_WordAt(text, name) is var full and >= 0)
        {
            return full;
        }

        var words = WordRegex().Matches(name).Select(match => match.Value).ToList();
        if (words.Count < 2 || words[0].Length < MinDistinctiveWordLength)
        {
            return -1;
        }

        var brand = words[0];
        var shared = projects.Any(other => other.Id != project.Id
            && WordRegex().Matches(other.Name).Any(match => string.Equals(match.Value, brand, StringComparison.OrdinalIgnoreCase)));
        return shared ? -1 : _WordAt(text, brand);
    }

    private static int _WordAt(string text, string words) =>
        Regex.Match(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(words)}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase) is { Success: true } match
            ? match.Index
            : -1;

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRegex();
}
