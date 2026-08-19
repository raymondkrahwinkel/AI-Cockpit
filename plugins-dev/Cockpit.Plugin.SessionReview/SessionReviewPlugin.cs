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
    // The intent another plugin opens this panel with (AC-961): the git-status badge in a session's header sends it
    // rather than referencing this plugin's types. Payload: the pane to review and its working directory.
    public const string OpenIntentAction = "open";

    public PluginMetadata Metadata { get; } = new(
        Id: "session-review",
        DisplayName: "Session Review",
        Author: "Cockpit",
        Description: "A \"Review changes\" action in each session's header — and a click on that session's git badge — "
            + "opens a panel showing what that session changed: a tree of changed files on the left, and on the right "
            + "one file at a time with old and new line numbers, coloured bands behind changed lines, and the changed "
            + "words picked out within a replaced line. Untracked files are included. One click asks the session to "
            + "review its own changes before they land. Requires git installed on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — the panel reads git on demand for the session it was opened from.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Review changes…",
            string.Empty,
            session => _ = _OpenAsync(host, session))
        {
            IconKind = MaterialIconKind.FileCompare,
        });

        host.RegisterIntentHandler(OpenIntentAction, intent =>
        {
            _ = _OpenAsync(host, new IntentSession(
                intent.Data.TryGetValue("paneId", out var pane) ? pane : string.Empty,
                intent.Data.TryGetValue("workingDirectory", out var directory) ? directory : null));

            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        });
    }

    // One review dialog per pane: reopening for the same session should refocus it, not stack another.
    private static Task _OpenAsync(ICockpitHost host, IPluginSessionContext session) => host.ShowDialogAsync(
        "Session review",
        () => new SessionDiffDialogControl(host, session),
        $"review.{session.PaneId}",
        // Wider and taller than the old flat list needed: the tree takes a fixed 260 on the left, and what
        // is left has to hold a line of code plus two number gutters without wrapping every other line.
        width: 1100,
        height: 720);

    public void Dispose()
    {
    }

    // An intent carries strings, not a live session, so the caller's pane and directory come across as a snapshot —
    // enough for the panel, which reads both only when it loads a diff.
    private sealed class IntentSession(string paneId, string? workingDirectory) : IPluginSessionContext
    {
        public string PaneId { get; } = paneId;

        public string? WorkingDirectory { get; } = workingDirectory;

        public event EventHandler? WorkingDirectoryChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<SessionOutputText>? OutputProduced
        {
            add { }
            remove { }
        }
    }
}
