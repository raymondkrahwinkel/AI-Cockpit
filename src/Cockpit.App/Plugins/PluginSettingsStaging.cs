using System.Diagnostics.CodeAnalysis;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.App.Plugins;

// The host side of the staged settings contract (AC-1003): plugin views hand over writes, held here until
// the operator confirms. No Revert method — a staged change was never written, so dropping it is the undo.
// Used both ways: a standalone window stages+commits on one click; Options stages the batch (AC-1001).
internal sealed class PluginSettingsStaging
{
    private readonly List<(string Tag, Func<Task> Commit)> _commits = [];

    public bool HasStagedChanges => _commits.Count > 0;

    // Validates one view and keeps its write for `CommitAsync`; false leaves nothing staged and fills `error`. `tag`
    // comes back on a failed commit so the caller can name which of its views it was. `onSaved` (AC-1004) is a
    // parameter, not something the caller runs afterwards, so it can never fire on a stage-only call.
    public bool TryStage(object view, string tag, Func<Task>? onSaved, [NotNullWhen(false)] out string? error)
    {
        if (!TryStage(view, out var commit, out error))
        {
            return false;
        }

        _commits.Add((tag, onSaved is null ? commit : async () => { await commit(); await onSaved(); }));
        return true;
    }

    // Runs every staged write, in the order it was staged, empties the batch, and reports the ones that threw.
    // AC-479: a throwing plugin used to take the whole Apply with it, including its neighbours' writes and the
    // host's own save. Its failure is returned rather than swallowed — the caller shows it where a refusal shows.
    public async Task<IReadOnlyList<(string Tag, string Reason)>> CommitAsync(Action<string>? onCommitted = null)
    {
        // Copied first: a plugin's own write can reach back into the host (re-registering an MCP server, say),
        // and iterating the live list would then throw on whatever it caused to be staged next.
        var commits = _commits.ToArray();
        _commits.Clear();
        var failures = new List<(string Tag, string Reason)>();
        foreach (var (tag, commit) in commits)
        {
            try
            {
                await commit();
            }
            catch (Exception exception)
            {
                failures.Add((tag, $"failed while saving: {exception.Message}"));
            }

            onCommitted?.Invoke(tag);
        }

        return failures;
    }

    // Drops everything staged. Nothing was written, so there is nothing else to undo.
    public void Revert() => _commits.Clear();

    // Whether the host offers this view a Save at all: the synchronous contract or its awaitable form (AC-1891).
    public static bool IsSettingsView(object? view) => view is IPluginSettingsView or IAsyncPluginSettingsView;

    // Where a plugin's answer is taken at face value or not. A refusal without a reason is the silent click
    // AC-499 went after, so it is answered rather than shown as an empty line; an acceptance without a write is
    // read as "nothing to save" so the operator's Save still closes the screen.
    internal static bool TryStage(object view, [NotNullWhen(true)] out Func<Task>? commit, [NotNullWhen(false)] out string? error)
    {
        // AC-479: a view that throws instead of refusing is answered as a refusal, so validating one plugin
        // cannot abort the operator's whole Apply before the other rows have even been asked.
        bool staged;
        try
        {
            staged = _Stage(view, out commit, out error);
        }
        catch (Exception exception)
        {
            commit = null;
            error = $"failed while checking its settings: {exception.Message}";
            return false;
        }

        if (staged)
        {
            commit ??= static () => Task.CompletedTask;
            return true;
        }

        commit = null;
        error = string.IsNullOrWhiteSpace(error)
            ? $"{view.GetType().Name} refused to save without giving a reason."
            : error;
        return false;
    }

    private static bool _Stage(object view, out Func<Task>? commit, out string? error)
    {
        if (view is IAsyncPluginSettingsView asyncView)
        {
            return asyncView.TryStage(out commit, out error);
        }

        if (view is IPluginSettingsView syncView)
        {
            var staged = syncView.TryStage(out var write, out error);
            commit = write is null ? null : () => { write(); return Task.CompletedTask; };
            return staged;
        }

        commit = null;
        error = $"{view.GetType().Name} has no settings to save.";
        return false;
    }
}
