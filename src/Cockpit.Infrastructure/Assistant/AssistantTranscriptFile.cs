using System.Text.Json;
using Microsoft.Extensions.Logging;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// The assistant's transcript as a JSON snapshot next to `cockpit.json` (AC-684) — what `ISessionStateStore`
// leaves out. Overwritten whole on every change, same idiom as `AssistantMemoryFile.NoteCurrentStateAsync`.
internal sealed class AssistantTranscriptFile : IAssistantTranscriptStore, ISingletonService
{
    // AC-947: enough to survive a crash-loop (each recovery route archives once) without the folder filling up.
    private const int MaxArchives = 3;

    private readonly string _filePath;
    private readonly ILogger<AssistantTranscriptFile> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public AssistantTranscriptFile(ILogger<AssistantTranscriptFile> logger)
        : this(CockpitConfigPath.AssistantTranscript, logger)
    {
    }

    // Test seam: point the store at an arbitrary file.
    internal AssistantTranscriptFile(string filePath, ILogger<AssistantTranscriptFile> logger)
    {
        _filePath = filePath;
        _logger = logger;
    }

    public async Task<IReadOnlyList<AssistantTranscriptSnapshotEntry>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_filePath);
            return await JsonSerializer.DeserializeAsync<IReadOnlyList<AssistantTranscriptSnapshotEntry>>(
                stream, cancellationToken: cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex)
        {
            // Derived state, same contract as SessionStateStore.LoadAsync: a file this build cannot read must not
            // stop the assistant from starting — it only starts without its history.
            _logger.LogWarning(ex, "Could not read the assistant transcript at {Path}.", _filePath);
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<AssistantTranscriptSnapshotEntry> entries, CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CockpitConfigPath.EnsurePrivateDirectory(Path.GetDirectoryName(_filePath) ?? CockpitConfigPath.Root);
            var content = JsonSerializer.Serialize(entries);

            // Atomic replace (sidecar + rename), the same idiom SessionStateStore.CompactAsync uses: a crash
            // mid-write must leave either the previous snapshot or the new one, never a half-written file the
            // next start cannot parse.
            CockpitConfigPath.ReplaceAtomicallyPrivate(_filePath, content);
        }
        catch (Exception ex)
        {
            // A transcript that could not be saved must not fail the turn that changed it — losing the snapshot
            // is bad, blocking the conversation over it is worse (same contract as SessionStateStore.RecordAsync).
            _logger.LogWarning(ex, "Could not save the assistant transcript at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task ArchiveAsync(CancellationToken cancellationToken = default)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                // A second archive-worthy start with no rows saved in between must not overwrite the real
                // archive with an empty one.
                return;
            }

            var directory = Path.GetDirectoryName(_filePath) ?? CockpitConfigPath.Root;
            var stem = Path.GetFileNameWithoutExtension(_filePath);
            var archivePath = Path.Combine(directory, $"{stem}.previous-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json");

            // A rename, not a copy-then-delete: the file keeps the owner-only mode `ReplaceAtomicallyPrivate` gave it.
            File.Move(_filePath, archivePath, overwrite: true);

            var stale = Directory.EnumerateFiles(directory, $"{stem}.previous-*.json")
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .Skip(MaxArchives);
            foreach (var path in stale)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Same contract as SaveAsync: an archive that could not be made must not stop the session from starting.
            _logger.LogWarning(ex, "Could not archive the assistant transcript at {Path}.", _filePath);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
