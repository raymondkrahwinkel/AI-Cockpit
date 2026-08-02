using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Core.Sessions;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Voice;

// The generic host-side transcript reader (Fase 4): a session's status tailer asks this by
// `SessionProfile`, and it dispatches to the profile's provider plugin — whichever registered a
// `TtyProviderRegistration.CreateTranscriptReader` — so the core carries no provider's transcript
// format or location. A profile-less session runs the bundled default provider's TUI, mirroring
// `TtySessionProviderResolver`; a profile whose provider records no transcript (or a local model
// that has no TUI) yields nothing, and the caller simply gets no status from a transcript.
internal sealed class SessionTranscriptReader(
    IServiceProvider services,
    IPluginTtyProviderRegistry ttyProviderRegistry) : ISessionTranscriptReader, ISingletonService
{
    public IReadOnlySet<string> SnapshotTranscripts(SessionProfile? profile) =>
        _ResolveReader(profile) is var (reader, configJson) && reader is not null
            ? reader.SnapshotTranscripts(configJson)
            : new HashSet<string>();

    public IAsyncEnumerable<SessionTranscriptActivity> ReadActivityAsync(
        SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, string? statusFile, CancellationToken cancellationToken) =>
        _ResolveReader(profile) is var (reader, configJson) && reader is not null
            ? _MapActivity(reader.ReadActivityAsync(configJson, knownTranscriptsAtLaunch, statusFile, cancellationToken))
            : _EmptyActivity();

    // The session's own already-written rows (AC-609), mapped onto the core's vocabulary. Keyed on the same
    // `statusFile` as the tail: a provider that cannot name this session's transcript reports nothing rather than
    // picking one, and the caller gets an empty slice instead of a stranger's conversation.
    public SessionTranscriptSlice ReadEntries(SessionProfile? profile, string? statusFile, int count)
    {
        var (reader, _) = _ResolveReader(profile);
        if (reader is null)
        {
            return SessionTranscriptSlice.Empty;
        }

        var slice = reader.ReadEntries(statusFile, count);
        return new SessionTranscriptSlice(
            [.. slice.Entries.Select(entry => new SessionTranscriptEntry(
                _MapEntryKind(entry.Kind), entry.Text, entry.ToolResult))],
            slice.TotalEntries);
    }

    // The plugin's coarse row kind under the host's own transcript name, so a TTY row and an SDK row of the same
    // sort read identically to whoever is looking at them. Spelled out rather than `ToString()`-ed: the two
    // vocabularies agreeing today is a coincidence worth being able to break.
    private static string _MapEntryKind(PluginTranscriptEntryKind kind) => kind switch
    {
        PluginTranscriptEntryKind.UserText => "UserText",
        PluginTranscriptEntryKind.AssistantText => "AssistantText",
        PluginTranscriptEntryKind.ToolUse => "ToolUse",
        PluginTranscriptEntryKind.ToolResult => "ToolResult",
        PluginTranscriptEntryKind.Thinking => "Thinking",
        _ => "Error",
    };

    // Maps the provider plugin's own activity signal to the core mirror the host consumes.
    private static async IAsyncEnumerable<SessionTranscriptActivity> _MapActivity(IAsyncEnumerable<PluginTranscriptActivity> source)
    {
        await foreach (var reading in source.ConfigureAwait(false))
        {
            var activity = reading.Activity switch
            {
                PluginSessionActivity.Busy => SessionActivity.Busy,
                PluginSessionActivity.BackgroundBusy => SessionActivity.BackgroundBusy,
                PluginSessionActivity.TurnComplete => SessionActivity.TurnComplete,
                _ => SessionActivity.None,
            };
            yield return new SessionTranscriptActivity(activity, reading.RawLine, _MapUsage(reading.Usage), reading.OutstandingShells);
        }
    }

    // The plugin-facing token usage (AC-398), mirrored onto the core type the same way the SDK path's own usage already is.
    private static TokenUsage? _MapUsage(PluginTokenUsage? usage) =>
        usage is null
            ? null
            : new TokenUsage(usage.InputTokens, usage.OutputTokens, usage.CacheReadInputTokens, usage.CacheCreationInputTokens);

    // The provider plugin's own reader for this profile and the config JSON to read it with, or a null reader
    // when the profile's provider registered none (a TUI that records nothing, or a local model with no TUI).
    // The profile→provider mapping mirrors `TtySessionProviderResolver`: a profile-less session runs
    // the bundled default provider, a plugin profile its own provider, and anything else has no TTY transcript.
    private (IPluginTranscriptReader? Reader, string ConfigJson) _ResolveReader(SessionProfile? profile)
    {
        var (providerId, configJson) = profile?.ProviderConfig switch
        {
            null => (ClaudePluginProfile.ProviderId, "{}"),
            PluginProviderConfig plugin => (plugin.ProviderId, plugin.ConfigJson),
            _ => (null, "{}"),
        };

        if (providerId is null || ttyProviderRegistry.Resolve(providerId)?.CreateTranscriptReader is not { } create)
        {
            return (null, configJson);
        }

        return (create(services), configJson);
    }

#pragma warning disable CS1998 // async iterator with no awaits — an immediately-completing empty stream
    private static async IAsyncEnumerable<SessionTranscriptActivity> _EmptyActivity([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}
