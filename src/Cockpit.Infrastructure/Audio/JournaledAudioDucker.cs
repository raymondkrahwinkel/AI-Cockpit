using System.Text.Json;
using Cockpit.Core.Abstractions.Audio;
using Cockpit.Core.Audio;
using Cockpit.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Cockpit.Infrastructure.Audio;

internal abstract class JournaledAudioDucker(ILogger logger, string journalPath) : ISystemAudioDucker
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _failed;
    private bool _restoreFailed;

    protected abstract Task<IReadOnlyList<AudioDuckingSession>> ReadSessionsAsync(CancellationToken cancellationToken);
    protected abstract Task SetVolumesAsync(AudioDuckingSession session, double[] volumes, CancellationToken cancellationToken);

    public async Task DuckAsync(double level, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_failed)
            {
                return;
            }

            IReadOnlyList<AudioDuckingSession> sessions = [];
            AudioDuckingSession? own = null;
            // SpeakingStarted precedes opening the first playback stream (AC-717).
            for (int attempt = 0; attempt < 10 && own is null; attempt++)
            {
                sessions = await ReadSessionsAsync(cancellationToken).ConfigureAwait(false);
                own = sessions.FirstOrDefault(session => session.ProcessId == Environment.ProcessId && session.IsPlaying);
                if (own is null)
                {
                    await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                }
            }
            if (own is null)
            {
                throw new InvalidOperationException("No active Cockpit audio session was found.");
            }

            double cockpitVolume = own.Volumes.Max();
            AudioDuckingEntry[] pending = File.Exists(journalPath)
                ? await _ReadJournalAsync(cancellationToken).ConfigureAwait(false) : [];
            AudioDuckingEntry[] entries = sessions
                .Where(session => session.ProcessId != Environment.ProcessId && session.IsPlaying && session.DeviceId == own.DeviceId)
                .Where(session => !pending.Any(entry => entry.Session.DeviceId == session.DeviceId
                    && (entry.Session.Id == session.Id || entry.Session.RestoreId.Length > 0 && entry.Session.RestoreId == session.RestoreId)))
                .Select(session => new AudioDuckingEntry(session,
                    session.Volumes.Select(volume => AudioDuckingPolicy.TargetVolume(volume, cockpitVolume, level)).ToArray()))
                .Where(entry => !entry.Session.Volumes.SequenceEqual(entry.Applied))
                .ToArray();
            if (entries.Length == 0)
            {
                return;
            }

            _SaveJournal([.. pending, .. entries]);
            foreach (AudioDuckingEntry entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await SetVolumesAsync(entry.Session, entry.Applied, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _Fail(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_restoreFailed || !File.Exists(journalPath))
            {
                return;
            }
            AudioDuckingEntry[] entries = await _ReadJournalAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<AudioDuckingSession> sessions = await ReadSessionsAsync(cancellationToken).ConfigureAwait(false);
            var remaining = new List<AudioDuckingEntry>();
            var restored = new HashSet<(string Device, string Id)>();
            foreach (AudioDuckingEntry entry in entries)
            {
                AudioDuckingSession[] candidates = sessions.Where(candidate => candidate.DeviceId == entry.Session.DeviceId
                    && candidate.ProcessId != Environment.ProcessId && !restored.Contains((candidate.DeviceId, candidate.Id))).ToArray();
                AudioDuckingSession? session = candidates.FirstOrDefault(candidate => candidate.Id == entry.Session.Id
                    && candidate.ProcessId == entry.Session.ProcessId && candidate.RestoreId == entry.Session.RestoreId);
                if (session is null && entry.Session.RestoreId.Length > 0)
                {
                    AudioDuckingSession[] matches = candidates.Where(candidate => candidate.RestoreId == entry.Session.RestoreId).ToArray();
                    session = matches.Length == 1 ? matches[0] : null;
                }
                if (session is null)
                {
                    if (entry.Session.RestoreId.Length > 0)
                    {
                        remaining.Add(entry);
                    }
                    continue;
                }
                if (session.Volumes.Length == entry.Applied.Length
                    && session.Volumes.Zip(entry.Applied).All(pair => AudioDuckingPolicy.ShouldRestore(pair.First, pair.Second)))
                {
                    await SetVolumesAsync(session, entry.Session.Volumes, cancellationToken).ConfigureAwait(false);
                }
                restored.Add((session.DeviceId, session.Id));
            }
            if (remaining.Count == 0)
            {
                File.Delete(journalPath);
            }
            else
            {
                _SaveJournal(remaining);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _restoreFailed = true;
            _Fail(exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AudioDuckingEntry[]> _ReadJournalAsync(CancellationToken cancellationToken) =>
        JsonSerializer.Deserialize<AudioDuckingEntry[]>(await File.ReadAllTextAsync(journalPath, cancellationToken).ConfigureAwait(false))
        ?? throw new InvalidDataException("The audio ducking journal is empty.");

    private void _SaveJournal(IReadOnlyList<AudioDuckingEntry> entries)
    {
        string temporary = journalPath + ".tmp";
        CockpitConfigPath.WriteAllTextPrivate(temporary, JsonSerializer.Serialize(entries), flushToDisk: true);
        File.Move(temporary, journalPath, overwrite: true);
    }

    private void _Fail(Exception exception)
    {
        if (!_failed)
        {
            logger.LogWarning(exception, "Audio ducking is unavailable; read-aloud continues without attenuation.");
            _failed = true;
        }
    }
}
