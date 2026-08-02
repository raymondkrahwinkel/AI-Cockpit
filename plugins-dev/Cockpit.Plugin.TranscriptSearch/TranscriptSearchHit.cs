namespace Cockpit.Plugin.TranscriptSearch;

// One match from a transcript search (#9): the `Role` that said it, a `Snippet` of
// the matching text (trimmed around the match), and where it came from — the `SessionId` (the
// transcript file's name without extension), the readable `Project` the session ran in, the
// `WorkingDirectory` it actually ran in (the `cwd` the transcript records, needed to resume
// it in the right place), and the `FilePath` on disk. `ModifiedUtc` is the transcript
// file's last-write time, so results can be shown most-recent-session first.
public sealed record TranscriptSearchHit(
    string SessionId,
    string Project,
    string Role,
    string Snippet,
    string FilePath,
    string? WorkingDirectory,
    DateTime ModifiedUtc);
