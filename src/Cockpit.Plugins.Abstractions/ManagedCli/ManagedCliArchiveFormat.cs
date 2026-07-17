namespace Cockpit.Plugins.Abstractions.ManagedCli;

/// <summary>
/// How the bytes a <see cref="ManagedCliDownloadPlan"/> points at are packaged (AC-20). Claude publishes the bare
/// executable per platform; Codex ships a gzip-compressed tarball per target-triple. The host's installer handles
/// both, so a descriptor only has to say which one its channel uses.
/// </summary>
public enum ManagedCliArchiveFormat
{
    /// <summary>The download <em>is</em> the executable — write it out, mark it executable, done (Claude).</summary>
    RawBinary,

    /// <summary>A <c>.tar.gz</c> holding the executable (and possibly siblings); extract the entry the plan names (Codex).</summary>
    TarGz,
}
