using System.Diagnostics.CodeAnalysis;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// The host side of the staged settings contract (AC-1003): plugin views hand over writes, held here until
// the operator confirms. No Revert method — a staged change was never written, so dropping it is the undo.
// Used both ways: a standalone window stages+commits on one click; Options stages the batch (AC-1001).
internal sealed class PluginSettingsStaging
{
    private readonly List<Action> _commits = [];

    public bool HasStagedChanges => _commits.Count > 0;

    // Validates one view and keeps its write for `Commit`; false leaves nothing staged and fills `error`.
    // `onSaved` (AC-1004) is a parameter, not something the caller runs afterwards, so it can never fire on a
    // stage-only call — only bound to the actual commit. Pass null when there's nothing to notify.
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
