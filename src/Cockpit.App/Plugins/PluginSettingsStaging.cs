using System.Diagnostics.CodeAnalysis;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// The host side of the staged settings contract (AC-1003): plugin views hand over writes, this holds them until
// the operator confirms.
//
// Reverting needs no plugin cooperation and has no method here for a reason — a staged change was never written,
// so dropping the collected commits *is* the undo. That is the whole reason the contract is one `TryStage` and
// not the stage/commit/revert triple: the two halves the host cannot do itself are validating and writing.
//
// Used both ways round. The standalone settings window (`PluginDialogHost.BuildSettingsFooter`) has nothing to
// wait for and calls the static `TryStage` straight through, committing on the same click. The instance half is
// for the Options dialog, which stages every plugin view it hosts, blocks Apply on the first refusal and commits
// the batch — the same shape `ApplyOptionsAsync` already gives `Profiles.PersistAsync` (AC-1001). Nothing is
// embedded in Options yet (that is the next ticket), so the batch side is exercised by its tests only.
internal sealed class PluginSettingsStaging
{
    private readonly List<Action> _commits = [];

    public bool HasStagedChanges => _commits.Count > 0;

    /// <summary>Validates one view and keeps its write for <see cref="Commit"/>; false leaves nothing staged and fills <paramref name="error"/>.</summary>
    public bool TryStage(IPluginSettingsView view, [NotNullWhen(false)] out string? error)
    {
        if (!TryStage(view, out var commit, out error))
        {
            return false;
        }

        _commits.Add(commit);
        return true;
    }

    /// <summary>Runs every staged write, in the order it was staged, and empties the batch.</summary>
    public void Commit()
    {
        // Copied first: a plugin's own write can reach back into the host (re-registering an MCP server, say),
        // and iterating the live list would then throw on whatever it caused to be staged next.
        var commits = _commits.ToArray();
        _commits.Clear();
        foreach (var commit in commits)
        {
            commit();
        }
    }

    /// <summary>Drops everything staged. Nothing was written, so there is nothing else to undo.</summary>
    public void Revert() => _commits.Clear();

    // Where a plugin's answer is taken at face value or not. A refusal without a reason is the silent click
    // AC-499 went after, so it is answered rather than shown as an empty line; an acceptance without a write is
    // read as "nothing to save" so the operator's Save still closes the screen.
    internal static bool TryStage(IPluginSettingsView view, [NotNullWhen(true)] out Action? commit, [NotNullWhen(false)] out string? error)
    {
        if (view.TryStage(out commit, out error))
        {
            commit ??= static () => { };
            return true;
        }

        commit = null;
        error = string.IsNullOrWhiteSpace(error)
            ? $"{view.GetType().Name} refused to save without giving a reason."
            : error;
        return false;
    }
}
