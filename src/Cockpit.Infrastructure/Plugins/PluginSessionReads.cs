using Cockpit.Core.Abstractions.Sessions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Plugins;

// AC-2256: the plugin session reads, one body for the desktop and backend observer. The pane is found under the
// launcher's exclusion and again right before the read, as PluginBackendHost._ActOnSessionAsync does (AC-1392);
// unlike there the assistant counts, since OpenSessions lists it.
public static class PluginSessionReads
{
    public static OpenCockpitSession Describe(ISessionHandle handle) =>
        new(handle.PaneId, handle.Title, handle.ActiveProfileLabel, handle.SessionStatus.ToString())
        {
            Statusline = _NullIfEmpty(handle.Statusline),
            IsTerminal = handle.IsTerminal,
            HasReadableTranscript = handle.HasReadableTranscript,
            AcceptsImages = handle.AcceptsImages,
        };

    public static async Task<PluginTranscriptSlice?> ReadTranscriptAsync(ISessionRegistry registry, ISessionLauncher? launcher, string paneId, int count, int? before = null)
    {
        if (await _FindAsync(registry, launcher, paneId).ConfigureAwait(false) is not { HasReadableTranscript: true } handle)
        {
            return null;
        }

        SessionTranscriptSlice slice = before is { } boundary
            ? await handle.ReadTranscriptBeforeAsync(boundary, count).ConfigureAwait(false)
            : await handle.ReadTranscriptAsync(count).ConfigureAwait(false);
        var first = slice.TotalEntries - slice.Entries.Count;
        return new PluginTranscriptSlice(
            [.. slice.Entries.Select((entry, position) => _Kind(entry.Kind) is { } kind
                ? new PluginTranscriptEntry(kind, entry.Text, entry.ToolResult) { Index = first + position }
                : null).OfType<PluginTranscriptEntry>()],
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

    // Host row kind to plugin kind, spelled out like SessionTranscriptReader._MapEntryKind; the host's own markers
    // (turn end, divider, status, turn result, suggestion) have no plugin row and are left out.
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
