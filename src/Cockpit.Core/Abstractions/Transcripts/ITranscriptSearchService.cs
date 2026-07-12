using Cockpit.Core.Transcripts;

namespace Cockpit.Core.Abstractions.Transcripts;

/// <summary>
/// Searches the on-disk <c>claude</c> session transcripts (#9): the JSONL files under
/// <c>&lt;config-dir&gt;/projects/*/*.jsonl</c> across the configured profiles' directories. A blank query
/// returns nothing; otherwise it returns the matching user/assistant lines as <see cref="TranscriptSearchHit"/>s,
/// most-recently-modified session first, capped so a huge history can't hang the UI.
/// </summary>
public interface ITranscriptSearchService
{
    Task<IReadOnlyList<TranscriptSearchHit>> SearchAsync(string query, int maxResults = 200, CancellationToken cancellationToken = default);
}
