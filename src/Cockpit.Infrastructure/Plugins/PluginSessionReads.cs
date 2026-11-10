using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// AC-2256: the session reads ICockpitSessionObserver offers a plugin, one body for the desktop and the backend
// observer. The pane is found under the launcher's exclusion and found again right before the read, as
// PluginBackendHost._ActOnSessionAsync does (AC-1392), so a pane that closed in between reads as unknown. Unlike
// that path the assistant counts: OpenSessions lists it, so a plugin may read it too.
public static class PluginSessionReads
{
    public static OpenCockpitSession Describe(ISessionHandle handle) =>
        new(handle.PaneId, handle.Title, handle.ActiveProfileLabel, handle.SessionStatus.ToString())
        {
            Statusline = _NullIfEmpty(handle.Statusline),
            IsTerminal = handle.IsTerminal,
            HasReadableTranscript = handle.HasReadableTranscript,
        };

    public static async Task<PluginTranscriptSlice?> ReadTranscriptAsync(ISessionRegistry registry, ISessionLauncher? launcher, string paneId, int count)
    {
        if (await _FindAsync(registry, launcher, paneId).ConfigureAwait(false) is not { HasReadableTranscript: true } handle)
        {
            return null;
        }

        var slice = await handle.ReadTranscriptAsync(count).ConfigureAwait(false);
        return new PluginTranscriptSlice(
            [.. slice.Entries.Select(entry => _Kind(entry.Kind) is { } kind ? new PluginTranscriptEntry(kind, entry.Text, entry.ToolResult) : null).OfType<PluginTranscriptEntry>()],
            slice.TotalEntries);
    }

    public static async Task<IReadOnlyList<PluginPendingPermission>> ReadPendingPermissionsAsync(ISessionRegistry registry, ISessionLauncher? launcher, string paneId)
    {
        if (await _FindAsync(registry, launcher, paneId).ConfigureAwait(false) is not { } handle)
        {
            return [];
        }

        var permissions = await handle.ReadPendingPermissionsAsync().ConfigureAwait(false);
        return [.. permissions.Select(permission => new PluginPendingPermission(permission.ToolUseId, permission.ToolName, permission.InputJson, permission.SinceUtc))];
    }

    private static async Task<ISessionHandle?> _FindAsync(ISessionRegistry registry, ISessionLauncher? launcher, string paneId)
    {
        if (string.IsNullOrEmpty(paneId))
        {
            return null;
        }

        ISessionHandle? Find() =>
            registry.Find(paneId) ?? (registry.Assistant is { } assistant && string.Equals(assistant.PaneId, paneId, StringComparison.Ordinal) ? assistant : null);

        var decided = launcher is null ? Find() : await launcher.RunExclusiveAsync(Find).ConfigureAwait(false);
        return decided is not null && ReferenceEquals(Find(), decided) ? decided : null;
    }

    // The host's row kind under the plugin's coarse one, spelled out as SessionTranscriptReader._MapEntryKind does the
    // other way. A question the agent asks is its prose, an inbox message is input to it; the host's own markers (a turn
    // ending, a divider, a status line, the files a turn changed) have no plugin row and are left out.
    private static PluginTranscriptEntryKind? _Kind(string kind) => kind switch
    {
        "UserText" or "Inbox" => PluginTranscriptEntryKind.UserText,
        "AssistantText" or "Question" => PluginTranscriptEntryKind.AssistantText,
        "ToolUse" => PluginTranscriptEntryKind.ToolUse,
        "ToolResult" => PluginTranscriptEntryKind.ToolResult,
        "Thinking" => PluginTranscriptEntryKind.Thinking,
        "Error" => PluginTranscriptEntryKind.Error,
        _ => null,
    };

    private static string? _NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;
}
