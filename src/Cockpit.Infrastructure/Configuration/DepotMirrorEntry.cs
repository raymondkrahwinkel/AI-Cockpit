using Cockpit.Core.Depot;

namespace Cockpit.Infrastructure.Configuration;

// AC-278: on-disk shape of a `DepotMirror`, mirroring how `RepositoryCloneEntry` shadows its record.
internal sealed class DepotMirrorEntry
{
    public string InstanceHost { get; set; } = string.Empty;

    public string Slug { get; set; } = string.Empty;

    public string Path { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public bool IsRetained { get; set; }

    public static DepotMirrorEntry FromDomain(DepotMirror record) => new()
    {
        InstanceHost = record.InstanceHost,
        Slug = record.Slug,
        Path = record.Path,
        CreatedAt = record.CreatedAt,
        IsRetained = record.IsRetained,
    };

    public DepotMirror ToDomain() => new(InstanceHost, Slug, Path, CreatedAt) { IsRetained = IsRetained };
}
