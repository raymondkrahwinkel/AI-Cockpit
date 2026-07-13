using Cockpit.Plugins.Abstractions;

namespace Cockpit.Plugin.TranscriptSearch;

/// <summary>
/// Searches the on-disk <c>claude</c> transcripts. It gathers the <c>projects</c> directories to scan — the CLI
/// default <c>~/.claude/projects</c> plus the <c>&lt;ConfigDir&gt;/projects</c> of every Claude-CLI profile the
/// cockpit has — then walks their <c>*.jsonl</c> files newest-first, pulling the searchable prose from each line
/// via <see cref="TranscriptTextExtractor"/> and collecting the case-insensitive matches. Per-file and total caps
/// keep a large history from hanging the UI; an unreadable file or directory is skipped, not fatal.
/// </summary>
internal sealed class TranscriptSearchService
{
    private const int MaxHitsPerFile = 20;

    /// <summary>The <see cref="PluginProfileInfo.Provider"/> of the profiles that keep transcripts.</summary>
    private const string ClaudeCliProvider = "ClaudeCli";

    /// <summary>
    /// Skips directories the operator cannot read instead of failing the walk: one unreadable folder anywhere
    /// under a profile's history would otherwise throw and take the whole search down with it. The
    /// <c>SearchOption</c> overloads do not do this — their compatibility options rethrow.
    /// </summary>
    private static readonly EnumerationOptions TranscriptFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
    };

    private readonly ICockpitHost? _host;
    private readonly IReadOnlyList<string>? _projectRootsOverride;

    public TranscriptSearchService(ICockpitHost host)
    {
        _host = host;
    }

    /// <summary>Test seam: search exactly these <c>projects</c> directories instead of resolving them from the host's profiles.</summary>
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

        var roots = await _ResolveProjectRootsAsync();
        var files = roots
            .SelectMany(_EnumerateTranscripts)
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
            await _SearchFileAsync(file, query, maxResults - hits.Count, hits, cancellationToken);
        }

        return hits;
    }

    // A root that has gone missing (a profile pointing at a deleted directory) or that we may not read yields no
    // transcripts, rather than failing the search across every other profile.
    private static IEnumerable<string> _EnumerateTranscripts(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, "*.jsonl", TranscriptFiles).ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static async Task _SearchFileAsync(FileInfo file, string query, int remaining, List<TranscriptSearchHit> hits, CancellationToken cancellationToken)
    {
        var sessionId = Path.GetFileNameWithoutExtension(file.Name);
        var project = file.Directory?.Name ?? string.Empty;
        var cap = Math.Min(MaxHitsPerFile, remaining);
        var found = 0;

        try
        {
            await foreach (var line in File.ReadLinesAsync(file.FullName, cancellationToken))
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

    // The CLI's own default directory plus the config directory of every Claude profile: a profile is an identity
    // with its own history, and reaching across them is the point of the search. Resolved per search, so a profile
    // added after the plugin loaded is included without a restart.
    private async Task<IReadOnlyList<string>> _ResolveProjectRootsAsync()
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

        if (_host is not null)
        {
            var profiles = await _host.GetProfilesAsync();
            roots.AddRange(profiles
                .Where(profile => profile.Provider == ClaudeCliProvider && !string.IsNullOrWhiteSpace(profile.ConfigDirectory))
                .Select(profile => Path.Combine(profile.ConfigDirectory, "projects")));
        }

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
