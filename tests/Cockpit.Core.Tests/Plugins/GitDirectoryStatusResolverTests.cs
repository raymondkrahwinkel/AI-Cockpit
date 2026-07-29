using Cockpit.App.Plugins;
using Cockpit.Plugins.Abstractions.Workspaces;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// The Autopilot isolation gate is fail-closed (AC-174): a run drops worktree isolation only for a directory that is
/// provably not a git repository. This resolver must therefore never report <see cref="GitDirectoryStatus.NotARepository"/>
/// from a git probe merely failing — a real repository git refused to read (dubious ownership, a lock, no commit yet)
/// has a <c>.git</c> and must stay <see cref="GitDirectoryStatus.Unknown"/> (which isolates), rather than run unisolated
/// in the real checkout.
/// </summary>
public sealed class GitDirectoryStatusResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cockpit-gitprobe-" + Guid.NewGuid().ToString("N"));

    public GitDirectoryStatusResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_WhenGitConfirmedARepository_IsRepository()
    {
        Assert.Equal(GitDirectoryStatus.Repository, GitDirectoryStatusResolver.Resolve(_root, gitConfirmedRepository: true));
    }

    [Fact]
    public void Resolve_WithNoGitAnywhereInTheTree_IsNotARepository()
    {
        // The one case that licenses running unisolated: a plain folder with no .git up the tree.
        Assert.Equal(GitDirectoryStatus.NotARepository, GitDirectoryStatusResolver.Resolve(_root, gitConfirmedRepository: false));
    }

    [Fact]
    public void Resolve_WhenAGitFolderExistsButTheProbeFailed_IsUnknown_NotNotARepository()
    {
        // A real repository git could not read (dubious ownership, a lock, no commit) — a .git is present but the probe
        // returned no repository. This must stay Unknown (isolate), never NotARepository (run free) — the C1 fail-open.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var nested = Path.Combine(_root, "src", "app");
        Directory.CreateDirectory(nested);

        Assert.Equal(GitDirectoryStatus.Unknown, GitDirectoryStatusResolver.Resolve(nested, gitConfirmedRepository: false));
    }

    [Fact]
    public void Resolve_WithAGitFileNotADirectory_IsUnknown_NotNotARepository()
    {
        // A worktree/submodule checkout has .git as a file, not a directory — it still means "under git", so a failed
        // probe there is Unknown, not NotARepository.
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: /somewhere/.git/worktrees/x");

        Assert.Equal(GitDirectoryStatus.Unknown, GitDirectoryStatusResolver.Resolve(_root, gitConfirmedRepository: false));
    }

    [Fact]
    public void Resolve_WithAMissingDirectory_IsUnknown_NotNotARepository()
    {
        Assert.Equal(GitDirectoryStatus.Unknown, GitDirectoryStatusResolver.Resolve(Path.Combine(_root, "does-not-exist"), gitConfirmedRepository: false));
    }

    [Fact]
    public void Resolve_WithABlankDirectory_IsUnknown()
    {
        Assert.Equal(GitDirectoryStatus.Unknown, GitDirectoryStatusResolver.Resolve("  ", gitConfirmedRepository: false));
    }

    [Fact]
    public void Resolve_ThroughASymlinkIntoARepository_IsUnknown_NotNotARepository()
    {
        // A symlink pointing into a repository must not read as "no .git": git walks the physical tree, so resolving the
        // link before the walk finds the .git and stays Unknown (isolate), never NotARepository (run free) — the C1
        // fail-open reached via a symlinked path (M1).
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var realSub = Path.Combine(_root, "src");
        Directory.CreateDirectory(realSub);

        var link = Path.Combine(Path.GetTempPath(), "cockpit-gitprobe-link-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, realSub);
        try
        {
            Assert.Equal(GitDirectoryStatus.Unknown, GitDirectoryStatusResolver.Resolve(link, gitConfirmedRepository: false));
        }
        finally
        {
            Directory.Delete(link);
        }
    }
}
