using Cockpit.Core.Clones;

namespace Cockpit.Infrastructure.Configuration;

/// <summary>
/// On-disk shape of a <see cref="RepositoryClone"/> under the <c>clones</c> section of <c>cockpit.json</c> (AC-90).
/// A plain DTO kept apart from the domain record so the persisted shape can evolve on its own, mirroring how
/// <see cref="WorktreeRegistryEntry"/> shadows the worktree record. <see cref="RemoteUrl"/> is stored credentials-free
/// — the parser strips any HTTPS userinfo before it is recorded — so no secret is ever written here.
/// </summary>
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
