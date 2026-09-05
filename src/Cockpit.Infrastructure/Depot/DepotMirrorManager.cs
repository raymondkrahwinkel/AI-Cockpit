using System.Security.Cryptography;
using System.Text;
using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Depot;
using Cockpit.Core.Depot;
using Cockpit.Core.Plugins;
using Cockpit.Infrastructure.Configuration;

namespace Cockpit.Infrastructure.Depot;

internal sealed class DepotMirrorManager : IDepotMirrorManager, ISingletonService
{
    private readonly IDepotMirrorRegistry _registry;

    // Resolves the mirrors root each time it is needed: the operator's override if set, else the state-root
    // default. Read on demand — not cached — so a root just changed in Options takes effect on the next mirror,
    // exactly as the clones and worktree roots do. The test seam pins a fixed root instead.
    private readonly Func<CancellationToken, Task<string>> _resolveRoot;

    public DepotMirrorManager(IDepotMirrorRegistry registry, IDepotMirrorSettingsStore settings)
    {
        _registry = registry;
        _resolveRoot = async cancellationToken =>
        {
            var root = (await settings.LoadAsync(cancellationToken).ConfigureAwait(false)).Root;
            return string.IsNullOrWhiteSpace(root) ? CockpitConfigPath.DepotMirrorsRoot : Path.GetFullPath(root);
        };
    }

    // Test seam: place mirrors under an arbitrary fixed root instead of resolving the operator's setting.
    internal DepotMirrorManager(IDepotMirrorRegistry registry, string mirrorsRoot)
    {
        _registry = registry;
        _resolveRoot = _ => Task.FromResult(mirrorsRoot);
    }

    public Task<string> GetEffectiveMirrorsRootAsync(CancellationToken cancellationToken = default) =>
        _resolveRoot(cancellationToken);

    public string BuildMirrorPath(string mirrorsRoot, string instanceHost, string slug) =>
        Path.GetFullPath(Path.Combine(mirrorsRoot, _SafeSegment(instanceHost), _SafeSegment(slug)));

    public async Task<DepotMirror> EnsureAsync(string instanceHost, string slug, CancellationToken cancellationToken = default)
    {
        var existing = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(record => _SameKey(record, instanceHost, slug));
        if (existing is not null)
        {
            // An existing mirror keeps the absolute path it was created at, even if the root override has since
            // changed — moving it would either duplicate content pull/push has not touched yet or orphan work
            // already pulled, and neither is this foundation's call to make.
            return existing;
        }

        var root = await _resolveRoot(cancellationToken).ConfigureAwait(false);
        var path = BuildMirrorPath(root, instanceHost, slug);
        Directory.CreateDirectory(path);

        var record = new DepotMirror(instanceHost, slug, path, DateTimeOffset.UtcNow);
        await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    public Task<IReadOnlyList<DepotMirror>> ListAsync(CancellationToken cancellationToken = default) =>
        _registry.ListAsync(cancellationToken);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);

        // Forget only the records whose folder is gone. A mirror folder that still exists is left exactly as it
        // is — it may hold local work no later ticket has synced yet, and this never deletes anything on disk
        // (cleanup-policy A, as the worktree and clone reconciles).
        foreach (var record in records.Where(record => !Directory.Exists(record.Path)))
        {
            await _registry.RemoveAsync(record.InstanceHost, record.Slug, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string?> RemoveAsync(DepotMirror record, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(record.Path) || !Directory.EnumerateFileSystemEntries(record.Path).Any())
        {
            await _registry.RemoveAsync(record.InstanceHost, record.Slug, cancellationToken).ConfigureAwait(false);
            return null;
        }

        // Local content this foundation has no way to prove is already synced elsewhere (pull/push are later
        // tickets) — kept and marked retained rather than dropped from the registry or deleted (cleanup-policy A).
        if (!record.IsRetained)
        {
            await _registry.AddAsync(record with { IsRetained = true }, cancellationToken).ConfigureAwait(false);
        }

        return $"The mirror for '{record.Slug}' was left on disk at '{record.Path}': it still holds local files. " +
            "Remove it by hand once you no longer need them, or turn mirroring back on to pick it up again.";
    }

    private static bool _SameKey(DepotMirror record, string instanceHost, string slug) =>
        string.Equals(record.InstanceHost, instanceHost, StringComparison.Ordinal)
        && string.Equals(record.Slug, slug, StringComparison.Ordinal);

    // PluginFolderName's own normalization when usable, else a deterministic hash of the raw value — so an
    // id Depot allows but a filesystem does not (unicode, punctuation, empty) still gets a stable folder.
    private static string _SafeSegment(string raw) =>
        PluginFolderName.Normalize(raw) is { Length: > 0 } normalized
            ? normalized
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..16];
}
