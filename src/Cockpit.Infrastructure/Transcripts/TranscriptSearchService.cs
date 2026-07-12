using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Profiles;
using Cockpit.Core.Abstractions.Transcripts;
using Cockpit.Core.Transcripts;

namespace Cockpit.Infrastructure.Transcripts;

/// <summary>
/// Searches the on-disk <c>claude</c> transcripts (#9). It gathers the <c>projects</c> directories to scan —
/// the CLI default <c>~/.claude/projects</c> plus each configured profile's <c>&lt;ConfigDir&gt;/projects</c> —
/// then walks their <c>*.jsonl</c> files newest-first, pulling the searchable prose from each line via
/// <see cref="TranscriptTextExtractor"/> and collecting the case-insensitive matches. Per-file and total caps
/// keep a large history from hanging the UI; an unreadable file (locked, mid-write) is skipped, not fatal.
/// </summary>
internal sealed class TranscriptSearchService : ITranscriptSearchService, ISingletonService
{
    private const int MaxHitsPerFile = 20;

    private readonly IClaudeProfileStore? _profileStore;
    private readonly IReadOnlyList<string>? _projectRootsOverride;

    public TranscriptSearchService(IClaudeProfileStore profileStore)
    {
        _profileStore = profileStore;
    }

    /// <summary>Test seam: search exactly these <c>projects</c> directories instead of resolving them from profiles/home.</summary>
    internal TranscriptSearchService(IReadOnlyList<string> projectRoots)
    {
        _projectRootsOverride = projectRoots;
    }

    public async Task<IReadOnlyList<TranscriptSearchHit>> SearchAsync(string query, int maxResults = 200, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var roots = await _ResolveProjectRootsAsync(cancellationToken).ConfigureAwait(false);
        var files = roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToList();

        var hits = new List<TranscriptSearchHit>();
        foreach (var file in files)
        {
            if (hits.Count >= maxResults)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _SearchFileAsync(file, query, maxResults - hits.Count, hits, cancellationToken).ConfigureAwait(false);
        }

        return hits;
    }

    private async Task _SearchFileAsync(FileInfo file, string query, int remaining, List<TranscriptSearchHit> hits, CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        var project = file.Directory?.Name ?? string.Empty;
        var cap = Math.Min(MaxHitsPerFile, remaining);
        var found = 0;

        try
        {
            await foreach (var line in File.ReadLinesAsync(file.FullName, cancellationToken).ConfigureAwait(false))
            {
                if (found >= cap)
                {
                    break;
                }

                if (TranscriptTextExtractor.Extract(line) is not { } entry
                    || entry.Text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                hits.Add(new TranscriptSearchHit(
                    sessionId,
                    project,
                    entry.Role,
                    TranscriptSnippet.Build(entry.Text, query),
                    file.FullName,
                    file.LastWriteTimeUtc));
                found++;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A transcript that is locked or mid-write is skipped rather than failing the whole search.
        }
    }

    private async Task<IReadOnlyList<string>> _ResolveProjectRootsAsync(CancellationToken cancellationToken)
    {
        if (_projectRootsOverride is not null)
        {
            return _projectRootsOverride;
        }

        var roots = new List<string>();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            roots.Add(Path.Combine(home, ".claude", "projects"));
        }

        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            roots.Add(Path.Combine(env, "projects"));
        }

        if (_profileStore is not null)
        {
            foreach (var profile in await _profileStore.LoadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(profile.ConfigDir))
                {
                    roots.Add(Path.Combine(profile.ConfigDir, "projects"));
                }
            }
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
