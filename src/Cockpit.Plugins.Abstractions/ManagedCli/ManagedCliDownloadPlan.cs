namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// What a <see cref="ManagedCliDescriptor"/> produces for one concrete (version, platform): the URL to fetch, the
/// SHA-256 to verify the fetched bytes against, and how to turn those bytes into an on-disk executable (AC-20). An
/// init-only record so a later field is a binary-safe addition to the plugin SDK (no positional constructor to break
/// an older compiled descriptor).
/// </summary>
public sealed record ManagedCliDownloadPlan
{
    /// <summary>Where the bytes come from — the raw binary (Claude) or the archive (Codex).</summary>
    public required string Url { get; init; }

    /// <summary>
    /// The expected SHA-256 (hex) of exactly the bytes at <see cref="Url"/>. Verified before anything is unpacked or
    /// placed; a mismatch is rejected and nothing is installed. Never optional — a managed CLI is executable code
    /// pulled over the network.
    /// </summary>
    public required string ExpectedSha256 { get; init; }

    /// <summary>The name the executable is given inside its version directory (e.g. <c>claude</c>, <c>claude.exe</c>, <c>codex</c>).</summary>
    public required string ExecutableFileName { get; init; }

    /// <summary>How <see cref="Url"/>'s bytes are packaged. Defaults to <see cref="ManagedCliArchiveFormat.RawBinary"/>.</summary>
    public ManagedCliArchiveFormat ArchiveFormat { get; init; } = ManagedCliArchiveFormat.RawBinary;

    /// <summary>
    /// For <see cref="ManagedCliArchiveFormat.TarGz"/>: the path of the executable entry inside the archive. When the
    /// archive holds a single file this may be left null and that file is taken. Ignored for a raw binary.
    /// </summary>
    public string? ExecutableEntryName { get; init; }

    /// <summary>Whether the placed file needs the Unix executable bit set (true for the Unix binaries; a no-op on Windows).</summary>
    public bool NeedsExecutableBit { get; init; }
}
