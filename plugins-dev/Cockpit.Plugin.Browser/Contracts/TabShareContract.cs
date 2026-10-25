using System.Text.Json;

namespace Cockpit.Plugin.Browser;

// AC-1714: the channel between the backend's MCP tools and the UI half, which holds the tabs and their grants. The
// UI decides every read: the backend only passes on the transport-verified caller and gets back an answer or a refusal.
internal static class TabShareContract
{
    // Event, backend to UI: a TabShareRequest from an agent's tool call.
    public const string Request = "share.request";

    // Action, UI to backend: the TabShareAnswer to a request, matched by its id.
    public const string Answer = "share.answer";

    // Action, UI to backend: the open sessions, for the share card's "With".
    public const string Sessions = "share.sessions";

    // Event, backend to UI: a session ended, so every grant it holds ends with it.
    public const string SessionClosed = "share.session-closed";

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

internal sealed record TabShareRequest(string Id, string PaneId, string Tool, string? TabId = null, string? Mode = null);

internal sealed record TabShareAnswer(
    string Id,
    bool Ok,
    string? Error = null,
    string? Origin = null,
    string? Url = null,
    string? Title = null,
    string? Text = null,
    string? ImagePng = null,
    IReadOnlyList<SharedTabInfo>? Tabs = null);

internal sealed record SharedTabInfo(string TabId, string Title, string Url, string Mode, bool Paused);
