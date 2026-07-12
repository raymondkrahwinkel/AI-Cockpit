namespace Cockpit.Core.Transcripts;

/// <summary>
/// One match from a transcript search (#9): the <see cref="Role"/> that said it, a <see cref="Snippet"/> of
/// the matching text (trimmed around the match), and where it came from — the <see cref="SessionId"/> (the
/// transcript file's name without extension), the readable <see cref="Project"/> the session ran in, and the
/// <see cref="FilePath"/> on disk. <see cref="ModifiedUtc"/> is the transcript file's last-write time, so
/// results can be shown most-recent-session first.
/// </summary>
public sealed record TranscriptSearchHit(
    string SessionId,
    string Project,
    string Role,
    string Snippet,
    string FilePath,
    DateTime ModifiedUtc);
