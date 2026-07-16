using System.Runtime.CompilerServices;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Voice;
using Cockpit.Core.Profiles;
using Cockpit.Infrastructure.Sessions.Tty;
using Cockpit.Plugins.Abstractions.Sessions;

namespace Cockpit.Infrastructure.Voice;

/// <summary>
/// The generic host-side transcript reader (Fase 4): a session's read-aloud and status tailers ask this by
/// <see cref="SessionProfile"/>, and it dispatches to the profile's provider plugin — whichever registered a
/// <see cref="TtyProviderRegistration.CreateTranscriptReader"/> — so the core carries no provider's transcript
/// format or location. A profile-less session runs the bundled default provider's TUI, mirroring
/// <see cref="TtySessionProviderResolver"/>; a profile whose provider records no transcript (or a local model
/// that has no TUI) yields nothing, and the caller simply gets no read-aloud/status from a transcript.
/// </summary>
internal sealed class SessionTranscriptReader(
    IServiceProvider services,
    IPluginTtyProviderRegistry ttyProviderRegistry) : ISessionTranscriptReader, ISingletonService
{
    public IReadOnlySet<string> SnapshotTranscripts(SessionProfile? profile) =>
        _ResolveReader(profile) is var (reader, configJson) && reader is not null
            ? reader.SnapshotTranscripts(configJson)
            : new HashSet<string>();

    public IAsyncEnumerable<string> ReadAssistantTextAsync(
        SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken) =>
        _ResolveReader(profile) is var (reader, configJson) && reader is not null
            ? reader.ReadAssistantTextAsync(configJson, knownTranscriptsAtLaunch, cancellationToken)
            : _Empty();

    public IAsyncEnumerable<string> ReadLinesAsync(
        SessionProfile? profile, IReadOnlySet<string> knownTranscriptsAtLaunch, CancellationToken cancellationToken) =>
        _ResolveReader(profile) is var (reader, configJson) && reader is not null
            ? reader.ReadLinesAsync(configJson, knownTranscriptsAtLaunch, cancellationToken)
            : _Empty();

    /// <summary>
    /// The provider plugin's own reader for this profile and the config JSON to read it with, or a null reader
    /// when the profile's provider registered none (a TUI that records nothing, or a local model with no TUI).
    /// The profile→provider mapping mirrors <see cref="TtySessionProviderResolver"/>: a profile-less session runs
    /// the bundled default provider, a plugin profile its own provider, and anything else has no TTY transcript.
    /// </summary>
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
    private static async IAsyncEnumerable<string> _Empty([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield break;
    }
#pragma warning restore CS1998
}
