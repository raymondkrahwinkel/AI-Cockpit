using System.Text;

namespace Cockpit.Plugin.GitStatus;

/// <summary>
/// Formats repository statuses (#1) as human/agent-readable text: a one-liner per repo for the injected
/// prompt, and a compact per-column string for the dialog grid.
/// </summary>
internal static class GitStatusSummary
{
    /// <summary>One repo as a phrase, e.g. "3 uncommitted changes, 2 unpushed commits" or "clean, up to date with upstream".</summary>
    public static string Describe(GitRepoStatus status)
    {
        if (status.Error is not null)
        {
            return $"error ({status.Error})";
        }

        var parts = new List<string>
        {
            status.Uncommitted == 0 ? "clean working tree" : $"{status.Uncommitted} uncommitted change(s)",
        };

        if (!status.HasUpstream)
        {
            parts.Add("no upstream");
        }
        else if (status.Ahead == 0 && status.Behind == 0)
        {
            parts.Add("up to date with upstream");
        }
        else
        {
            if (status.Ahead > 0)
            {
                parts.Add($"{status.Ahead} unpushed commit(s)");
            }

            if (status.Behind > 0)
            {
                parts.Add($"{status.Behind} behind upstream");
            }
        }

        return string.Join(", ", parts);
    }

    /// <summary>The compact ahead/behind column, e.g. "↑2 ↓1", "↑3", "—" (up to date) or "no upstream".</summary>
    public static string RemoteState(GitRepoStatus status)
    {
        if (status.Error is not null)
        {
            return "—";
        }

        if (!status.HasUpstream)
        {
            return "no upstream";
        }

        if (status.Ahead == 0 && status.Behind == 0)
        {
            return "—";
        }

        var parts = new List<string>();
        if (status.Ahead > 0)
        {
            parts.Add($"↑{status.Ahead}");
        }

        if (status.Behind > 0)
        {
            parts.Add($"↓{status.Behind}");
        }

        return string.Join(" ", parts);
    }

    /// <summary>The multi-repo block dropped into the active session.</summary>
    public static string Render(IReadOnlyList<GitRepoStatus> statuses)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Here is the current git status of my repositories:");
        foreach (var status in statuses)
        {
            builder.AppendLine($"- {status.Name} ({status.Path}) on '{status.Branch}': {Describe(status)}");
        }

        return builder.ToString().TrimEnd();
    }
}
