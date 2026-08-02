using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.SessionReview;

// Per-session diff/review panel (AC-50): adds a "Review changes…" action to each session's header that opens a panel
// showing the uncommitted git diff of that session's working directory, with one click to ask the session to review
// its own changes. Makes the cockpit a review station — the quality guard before an agent's output lands. No local
// state, so `ConfigureServices` is empty.
public sealed class SessionReviewPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "session-review",
        DisplayName: "Session Review",
        Author: "Cockpit",
        Description: "A \"Review changes\" action in each session's header opens a panel showing what that session "
            + "changed: a tree of changed files on the left, and on the right one file at a time with old and new line "
            + "numbers, coloured bands behind changed lines, and the changed words picked out within a replaced line. "
            + "Untracked files are included. One click asks the session to review its own changes before they land. "
            + "Requires git installed on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — the panel reads git on demand for the session it was opened from.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Review changes…",
            string.Empty,
            // One review dialog per pane: reopening for the same session should refocus it, not stack another.
            session => _ = host.ShowDialogAsync(
                "Session review",
                () => new SessionDiffDialogControl(host, session),
                $"review.{session.PaneId}",
                // Wider and taller than the old flat list needed: the tree takes a fixed 260 on the left, and what
                // is left has to hold a line of code plus two number gutters without wrapping every other line.
                width: 1100,
                height: 720))
        {
            IconKind = MaterialIconKind.FileCompare,
        });
    }

    public void Dispose()
    {
    }
}
