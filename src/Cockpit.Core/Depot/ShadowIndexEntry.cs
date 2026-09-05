namespace Cockpit.Core.Depot;

// One file's local shadow state (AC-281): the Depot checksum this file had when last pulled, and the local
// working file's own size/mtime at that moment. The pull engine compares a file's *current* size/mtime against
// these to tell whether the operator touched it since — a stat check, not a re-hash on every pull.
public sealed record ShadowIndexEntry(string Path, string BaseChecksum, long Size, DateTimeOffset Mtime);
