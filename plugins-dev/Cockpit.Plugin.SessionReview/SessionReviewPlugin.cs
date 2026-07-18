using Microsoft.Extensions.DependencyInjection;
using Material.Icons;
using Cockpit.Plugins.Abstractions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Plugin.SessionReview;

/// <summary>
/// Per-session diff/review panel (AC-50): adds a "Review changes…" action to each session's header that opens a panel
/// showing the uncommitted git diff of that session's working directory, with one click to ask the session to review
/// its own changes. Makes the cockpit a review station — the quality guard before an agent's output lands. No local
/// state, so <see cref="ConfigureServices"/> is empty.
/// </summary>
public sealed class SessionReviewPlugin : ICockpitPlugin
{
    public PluginMetadata Metadata { get; } = new(
        Id: "session-review",
        DisplayName: "Session Review",
        Version: "0.1.0",
        Author: "Cockpit",
        Description: "A \"Review changes\" action in each session's header opens a panel showing what that session "
            + "changed (the git diff of its working directory, coloured for reading), with one click to ask the session "
            + "to review its own changes before they land. Requires git installed on the machine running Cockpit.");

    public void ConfigureServices(IServiceCollection services)
    {
        // No local state or background services — the panel reads git on demand for the session it was opened from.
    }

    public void Initialize(ICockpitHost host)
    {
        host.AddSessionHeaderAction(new PluginSessionAction(
            "Review changes…",
            string.Empty,
            session => _ = host.ShowDialogAsync(
                "Session review",
                () => new SessionDiffDialogControl(host, session),
                860,
                620))
        {
            IconKind = MaterialIconKind.FileCompare,
        });
    }

    public void Dispose()
    {
    }
}
