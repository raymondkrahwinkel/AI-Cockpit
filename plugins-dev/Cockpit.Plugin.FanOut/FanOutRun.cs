using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Plugin.FanOut;

/// <summary>
/// One fan-out the operator has set up: the task, where it runs, and the arms to run it on. Turning that into
/// the sessions to start is <see cref="ToRequests"/> — kept apart from the surface that draws them so what a
/// run actually asks the host for is readable, and testable, without a window.
/// </summary>
/// <param name="Task">The one task every arm is given.</param>
/// <param name="WorkingDirectory">The repository the run works in; empty runs each arm in the app's own working directory.</param>
/// <param name="Variants">The arms, two to five of them. Each becomes one session in its own worktree.</param>
public sealed record FanOutRun(string Task, string WorkingDirectory, IReadOnlyList<FanOutVariant> Variants)
{
    /// <summary>The fewest arms that still make it a fan-out: one session compared against nothing is an ordinary session.</summary>
    public const int MinimumVariants = 2;

    /// <summary>Where the tiles stop being readable, and the ceiling the ticket sets on a run.</summary>
    public const int MaximumVariants = 5;

    public bool CanStart => Task.Trim().Length > 0 && Variants.Count is >= MinimumVariants and <= MaximumVariants;

    /// <summary>
    /// What to ask the host to start, one request per arm. Every arm isolates in its own worktree — that is what
    /// makes the arms comparable afterwards rather than a race to edit the same files — and carries the run's id,
    /// so the cockpit records what the whole run spent instead of a handful of unrelated sessions.
    /// </summary>
    /// <param name="runId">Stable for the lifetime of this run; the same value on every session it starts.</param>
    public IReadOnlyList<EmbeddedSessionRequest> ToRequests(string runId)
    {
        var workingDirectory = WorkingDirectory.Trim();
        var label = FanOutBrief.Label(Task);

        return Variants
            .Select(variant => new EmbeddedSessionRequest
            {
                ProfileId = variant.ProfileId.Trim() is { Length: > 0 } profile ? profile : null,
                WorkingDirectory = workingDirectory.Length > 0 ? workingDirectory : null,
                IsolateInWorktree = true,
                InitialUserMessage = FanOutBrief.Compose(Task, variant.Angle),
                RunId = runId,
                RunLabel = label,
            })
            .ToList();
    }
}
