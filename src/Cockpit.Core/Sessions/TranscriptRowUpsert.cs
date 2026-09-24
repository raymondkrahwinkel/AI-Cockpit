namespace Cockpit.Core.Sessions;

// One row's new version, numbered on the backend-wide event counter so a stream can resume from a `Last-Event-ID` (F5).
// The version counts this row's upserts; the store keeps no version of its own, its last line wins.
public readonly record struct TranscriptRowUpsert(long Seq, int Version, TranscriptSnapshotEntry Row);
