using System.Text.Json;

namespace Cockpit.Plugin.SessionReview.Contracts;

// AC-1395: what the backend part tells the UI part over the plugin's channel. Compiled into both assemblies as a
// linked source file rather than shared as an assembly, so the UI part never references the backend part.
internal static class SessionReviewChannel
{
    // Event: published when another plugin's intent (SessionReviewPlugin.OpenIntentAction) asks to open the
    // review panel — the backend has no window to open it in. Payload: SessionReviewOpenRequest.
    public const string OpenEvent = "open";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

// What the intent handed the backend, carried across to the UI part unchanged: the pane to review and its
// working directory.
internal sealed record SessionReviewOpenRequest(string PaneId, string? WorkingDirectory);
