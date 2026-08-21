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
// Used both ways round, and by both hosts through the same instance. The standalone settings window
// (`PluginDialogHost.BuildSettingsFooter`) has nothing to wait for, so it stages and commits on the same click;
// the Options dialog stages every plugin view it hosts, blocks Apply on the first refusal and commits the batch —
// the same shape `ApplyOptionsAsync` already gives `Profiles.PersistAsync` (AC-1001). Nothing is embedded in
// Options yet (that is the next ticket), so the batch side is exercised by its tests only.
internal sealed class PluginSettingsStaging
{
    private readonly List<Action> _commits = [];

    public bool HasStagedChanges => _commits.Count > 0;

    // Validates one view and keeps its write for `Commit`; false leaves nothing staged and fills `error`.
    //
    // `onSaved` — a plugin's `ICockpitHost.OnSettingsSaved` subscribers (AC-1004) — is a parameter rather than
    // something the caller runs afterwards because it must fire *after* the write and never on a stage. Four
    // plugins hang a cache invalidation off that signal (Docker's engine, LocalCi's runtime, Kubernetes'
    // connections, GitHub PR's refresh); fired while the values were only staged, each would rebuild against the
    // settings the operator just replaced. Bound to the commit here, no host can hold the two apart by accident.
    // Pass null for a view whose host has nothing to notify.
    public bool TryStage(IPluginSettingsView view, Action? onSaved, [NotNullWhen(false)] out string? error)
    {
        if (!TryStage(view, out var commit, out error))
        {
            return false;
        }

        _commits.Add(onSaved is null ? commit : () => { commit(); onSaved(); });
        return true;
    }

    // Runs every staged write, in the order it was staged, and empties the batch.
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

    // Drops everything staged. Nothing was written, so there is nothing else to undo.
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
