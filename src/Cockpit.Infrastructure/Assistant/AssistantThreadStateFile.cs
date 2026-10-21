using System.Text.Json;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Assistant;
using Cockpit.Core.Assistant;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Assistant;

// Assistant threads: the closed set beside the assistant's own transcript log (`transcripts/cockpit-assistant.threads.json`),
// so it lives in the state root like the conversation does — per operator, never synced. Not in `AssistantSettings`:
// nothing but the operator writes those (AssistantSettingsWritersTests), and close_thread is the assistant's tool.
internal sealed class AssistantThreadStateFile : IAssistantThreadStateStore, ISingletonService
{
    private readonly string _path;

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AssistantThreadStateFile()
        : this(Path.Combine(CockpitConfigPath.TranscriptsRoot, $"{AssistantIdentity.PaneId}.threads.json"))
    {
    }

    // Test seam: point the store at an arbitrary file.
    internal AssistantThreadStateFile(string path) => _path = path;

    public async Task<IReadOnlyList<string>> LoadClosedAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            var saved = await JsonSerializer.DeserializeAsync<SavedThreads>(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            return saved?.Closed ?? [];
        }
        catch (JsonException)
        {
            // A file this build cannot read leaves every thread open, which hides nothing.
            return [];
        }
        finally
        {
            _gate.Release();
        }
    }

    // Written aside and moved over, so a crash mid-write leaves the old set rather than half a file.
    public async Task SaveClosedAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new SavedThreads([.. projectIds.Order(StringComparer.Ordinal)])), cancellationToken)
                .ConfigureAwait(false);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private sealed record SavedThreads(IReadOnlyList<string> Closed);
}
