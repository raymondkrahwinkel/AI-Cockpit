using Cockpit.Core.Clones;

namespace Cockpit.Infrastructure.Configuration;

// AC-90: on-disk shape of a `RepositoryClone`, mirroring how `WorktreeRegistryEntry` shadows its record.
// `RemoteUrl` is stored credentials-free — the parser strips any HTTPS userinfo before recording it.
internal sealed class RepositoryCloneEntry
{
    public string Slug { get; set; } = string.Empty;

    public string RemoteUrl { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset LastUsedAt { get; set; }

    public static RepositoryCloneEntry FromDomain(RepositoryClone record) => new()
    {
        Slug = record.Slug,
        RemoteUrl = record.RemoteUrl,
        Path = record.Path,
        CreatedAt = record.CreatedAt,
        LastUsedAt = record.LastUsedAt,
    };

    public RepositoryClone ToDomain() => new(Slug, RemoteUrl, Path, CreatedAt, LastUsedAt);
}
