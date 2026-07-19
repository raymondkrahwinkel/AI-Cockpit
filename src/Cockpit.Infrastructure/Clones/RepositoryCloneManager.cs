using Cockpit.Core.Abstractions;
using Cockpit.Core.Abstractions.Clones;
using Cockpit.Core.Clones;
using Cockpit.Infrastructure.Configuration;
using Cockpit.Infrastructure.Worktrees;

namespace Cockpit.Infrastructure.Clones;

/// <inheritdoc cref="IRepositoryCloneManager" />
internal sealed class RepositoryCloneManager : IRepositoryCloneManager, ISingletonService
{
    // Turns off git's interactive credential prompting for every clone/fetch: without a helper (or headless) git
    // fails fast with a message instead of blocking on a terminal prompt no window can answer. This is v1's whole
    // auth story — lean on the host credential helper (GCM, `gh`) — and the seam a later in-memory token injection
    // (AC-88: GIT_ASKPASS plus the token in this child env only) extends, never a token in the URL.
    private static readonly IReadOnlyDictionary<string, string> _NonInteractiveEnvironment =
        new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };

    private readonly IRepositoryCloneRegistry _registry;
    private readonly string _clonesRoot;

    public RepositoryCloneManager(IRepositoryCloneRegistry registry)
        : this(registry, CockpitConfigPath.ClonesRoot)
    {
    }

    /// <summary>Test seam: place the clones under an arbitrary fixed root instead of the app state directory.</summary>
    internal RepositoryCloneManager(IRepositoryCloneRegistry registry, string clonesRoot)
    {
        _registry = registry;
        _clonesRoot = clonesRoot;
    }

    public async Task<RepositoryClone> CloneAsync(string url, CancellationToken cancellationToken = default)
    {
        var parsed = GitCloneUrl.Parse(url);
        var targetPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(_clonesRoot, parsed.RelativePath));

        // De-dup: the slug is already occupied. If it holds the same repository, reuse it (fetch it up to date)
        // rather than cloning again; if it holds a *different* one, refuse — never clobber a checkout that might
        // hold work. Authoritative on the filesystem (the repo's own origin remote), so it is right even if the
        // registry drifted from disk.
        if (Directory.Exists(targetPath))
        {
            if (await _IsSameRepositoryAsync(targetPath, parsed, cancellationToken).ConfigureAwait(false))
            {
                return await _ReuseAsync(parsed, targetPath, cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"A different repository is already cloned at '{targetPath}'. Remove it first to clone {parsed.Slug} there.");
        }

        var parent = System.IO.Path.GetDirectoryName(targetPath)!;
        Directory.CreateDirectory(parent);

        // clone with an explicit "--" so a URL that begins with "-" can never be read as an option, and the target
        // path spelled out so the checkout lands exactly under the managed slug. RemoteUrl is credentials-free for
        // HTTPS, so nothing secret reaches argv or the resulting .git/config.
        try
        {
            await GitCli.RunCheckedAsync(
                parent,
                ["clone", "--", parsed.RemoteUrl, targetPath],
                cancellationToken,
                _NonInteractiveEnvironment).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(_ExplainFailure(exception.Message, parsed.Host), exception);
        }

        var now = DateTimeOffset.UtcNow;
        var record = new RepositoryClone(parsed.Slug, parsed.RemoteUrl, targetPath, now, now);
        await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);

        return record;
    }

    public Task<IReadOnlyList<RepositoryClone>> ListAsync(CancellationToken cancellationToken = default) =>
        _registry.ListAsync(cancellationToken);

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var records = await _registry.ListAsync(cancellationToken).ConfigureAwait(false);

        // Forget only the records whose folder is gone. A clone that still exists is left exactly as it is — it may
        // hold uncommitted work, and this reconcile never deletes anything on disk (cleanup-policy A, as AC-85).
        foreach (var record in records.Where(record => !Directory.Exists(record.Path)))
        {
            await _registry.RemoveAsync(record.Path, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<RepositoryClone> _ReuseAsync(GitCloneUrl parsed, string targetPath, CancellationToken cancellationToken)
    {
        // Best-effort refresh: a reused clone should not be stale, but a fetch that fails (offline, auth lapsed) must
        // not deny the operator the checkout that is already here — the local repository is still usable.
        await GitCli.RunAsync(targetPath, ["fetch", "--all", "--prune"], cancellationToken, _NonInteractiveEnvironment)
            .ConfigureAwait(false);

        var existing = (await _registry.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(record => string.Equals(
                System.IO.Path.GetFullPath(record.Path), targetPath, _PathComparison));

        var now = DateTimeOffset.UtcNow;
        var record = existing is null
            ? new RepositoryClone(parsed.Slug, parsed.RemoteUrl, targetPath, now, now)
            : existing with { LastUsedAt = now };

        // Upsert so the registry adopts a clone that was on disk but unrecorded, and bumps LastUsedAt otherwise.
        await _registry.AddAsync(record, cancellationToken).ConfigureAwait(false);

        return record;
    }

    private static async Task<bool> _IsSameRepositoryAsync(string targetPath, GitCloneUrl parsed, CancellationToken cancellationToken)
    {
        var insideWorkTree = await GitCli.RunAsync(targetPath, ["rev-parse", "--is-inside-work-tree"], cancellationToken)
            .ConfigureAwait(false);
        if (insideWorkTree.ExitCode != 0 || insideWorkTree.StandardOutput.Trim() != "true")
        {
            return false;
        }

        var remote = await GitCli.RunAsync(targetPath, ["remote", "get-url", "origin"], cancellationToken).ConfigureAwait(false);
        return remote.ExitCode == 0 && parsed.SameRepositoryAs(remote.StandardOutput.Trim());
    }

    // Turns git's raw failure into something the operator can act on, without echoing the URL (which could carry a
    // token). Only the two cases v1 can recognise get a hint; anything else is git's own message, already actionable.
    private static string _ExplainFailure(string gitMessage, string host)
    {
        if (gitMessage.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || gitMessage.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
            || gitMessage.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase))
        {
            return $"{gitMessage}\n\nCockpit clones through your own git credential helper. Configure one for {host} "
                + "(Git Credential Manager, or `gh auth login`) and try again.";
        }

        if (gitMessage.Contains("SAML", StringComparison.OrdinalIgnoreCase)
            || gitMessage.Contains("SSO", StringComparison.OrdinalIgnoreCase))
        {
            return $"{gitMessage}\n\nThe token needs SAML SSO authorization for this organization — authorize it in "
                + "the provider's token settings, then try again.";
        }

        return gitMessage;
    }

    private static readonly StringComparison _PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
