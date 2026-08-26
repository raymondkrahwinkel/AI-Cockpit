namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// One sibling binary a <see cref="ManagedCliDownloadPlan"/> promises alongside its primary executable (AC-1107) —
/// e.g. Codex's separate <c>codex-code-mode-host</c>. Same shape as the plan's own download fields, since placing
/// one is the same operation: fetch, verify SHA-256, unpack if needed, write under the version directory.
/// </summary>
public sealed record ManagedCliDownloadArtifact
{
    /// <summary>
    /// Where the bytes come from.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// The expected SHA-256 (hex) of exactly the bytes at <see cref="Url"/>. Never optional, same as the plan's own.
    /// </summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>
    /// The name this artifact is given inside the version directory (e.g. <c>codex-code-mode-host.exe</c>).
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// How <see cref="Url"/>'s bytes are packaged. Defaults to <see cref="ManagedCliArchiveFormat.RawBinary"/>.
    /// </summary>
    public ManagedCliArchiveFormat ArchiveFormat { get; init; } = ManagedCliArchiveFormat.RawBinary;

    /// <summary>
    /// For <see cref="ManagedCliArchiveFormat.TarGz"/>: the archive entry to extract. Left null for a single-file archive.
    /// </summary>
    public string? ArchiveEntryName { get; init; }

    /// <summary>
    /// Whether the placed file needs the Unix executable bit set.
    /// </summary>
    public bool NeedsExecutableBit { get; init; }
}
