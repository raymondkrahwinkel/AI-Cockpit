namespace Cockpit.Plugins.Abstractions.Backup;

/// <summary>
/// Somewhere a cockpit backup can be kept (AC-1501). The host builds the archive and hands it over as a stream, and
/// reads it back the same way — a target never sees a path of the host's, and need not have one of its own.
/// </summary>
public interface IBackupTarget
{
    /// <summary>A stable id for this target, unique among the targets one host knows.</summary>
    string Id { get; }

    /// <summary>What the operator sees this target called when they pick where a backup goes.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Keeps <paramref name="archive"/> under <paramref name="name"/> — a suggestion a target without names may
    /// ignore — and returns the id <see cref="OpenReadAsync"/> reads it back by. A write that fails or is cancelled
    /// leaves nothing behind that <see cref="ListAsync"/> would show: a half archive is a backup trusted exactly once.
    /// </summary>
    Task<string> WriteAsync(string name, Stream archive, CancellationToken cancellationToken = default);

    /// <summary>The backups this target holds, in no particular order.</summary>
    Task<IReadOnlyList<BackupTargetEntry>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens the backup <paramref name="id"/> for reading; the caller disposes the stream.</summary>
    Task<Stream> OpenReadAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the backup <paramref name="id"/> and returns true, or returns false when this target cannot delete —
    /// the default, so a target written before retention (AC-1502) keeps compiling and is never pruned.
    /// </summary>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(false);
}

/// <summary>
/// One backup a target holds: the id to read it back by, when it was made, its size in bytes, and the version of the
/// cockpit that made it — null when the target cannot tell.
/// </summary>
public sealed record BackupTargetEntry(string Id, DateTimeOffset CreatedAt, long Size, string? AppVersion);
