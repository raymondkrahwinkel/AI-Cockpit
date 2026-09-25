using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Cockpit.Plugin.SessionReview.Contracts;
using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.SessionReview;

// Per-session diff/review panel (AC-50): the backend part. It adds a "Review changes…" action to each session's
// header that opens a panel showing the uncommitted git diff of that session's working directory — everything
// that action needs (the panel, its dialog) lives in the UI part, SessionReviewUi (AC-1395), since it needs a
// window. The backend keeps the intent handler: RegisterIntentHandler has no ICockpitUiHost equivalent, so the
// git-status badge still reaches this plugin through it, and the backend forwards the open request to the UI
// part over the plugin's own channel. No local state, so `ConfigureServices` is empty.
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
        host.RegisterIntentHandler(OpenIntentAction, intent =>
        {
            var request = new SessionReviewOpenRequest(
                intent.Data.TryGetValue("paneId", out var pane) ? pane : string.Empty,
                intent.Data.TryGetValue("workingDirectory", out var directory) ? directory : null);
            host.Channel.Publish(SessionReviewChannel.OpenEvent, JsonSerializer.SerializeToElement(request, SessionReviewChannel.Json));

            return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        });
    }

    public void Dispose()
    {
    }
}
