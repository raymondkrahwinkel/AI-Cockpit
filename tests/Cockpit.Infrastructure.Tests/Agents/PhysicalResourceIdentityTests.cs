using Cockpit.Infrastructure.Agents;

namespace Cockpit.Infrastructure.Tests.Agents;

/// <summary>
/// AC-439's answer to "what counts as the same physical resource": a rooted path that exists on disk is resolved
/// (full path, leaf symlink collapsed); anything else — a branch name, a relative path, a rooted path that no longer
/// exists — is compared exactly as written, the same rule AC-393's own exact-match claims already apply. Reaching
/// into the class under test via its internal accessibility (this test project has <c>InternalsVisibleTo</c> on
/// <c>Cockpit.Infrastructure</c>, the same way <c>AgentResourceClaimsTests</c> reaches <c>AgentResourceClaims</c>).
/// </summary>
public sealed class PhysicalResourceIdentityTests
{
    [Fact]
    public void Canonicalize_ANonRootedResource_IsReturnedUnchanged()
    {
        Assert.Equal("feature/AC-439", PhysicalResourceIdentity.Canonicalize("feature/AC-439"));
    }

    [Fact]
    public void Canonicalize_ARootedPathThatDoesNotExist_IsReturnedUnchanged()
    {
        // No filesystem entry to resolve against, so this is the honest fallback: compared exactly, same as any
        // other free-text resource. Two different spellings of a path that has been removed (a worktree already torn
        // down) therefore stay two different groups — the accepted phase-1 gap, not a bug.
        var path = Path.Combine(Path.GetTempPath(), $"ac439-missing-{Guid.NewGuid():N}");

        Assert.Equal(path, PhysicalResourceIdentity.Canonicalize(path));
    }

    [Fact]
    public void Canonicalize_AnExistingDirectory_ResolvesATrailingSeparator()
    {
        var real = Directory.CreateTempSubdirectory("ac439-canon-");
        try
        {
            var spelledWithTrailingSeparator = real.FullName + Path.DirectorySeparatorChar;

            Assert.Equal(
                PhysicalResourceIdentity.Canonicalize(real.FullName),
                PhysicalResourceIdentity.Canonicalize(spelledWithTrailingSeparator));
        }
        finally
        {
            real.Delete();
        }
    }

    [Fact]
    public void Canonicalize_AnExistingDirectory_ResolvesARelativeSegment()
    {
        var real = Directory.CreateTempSubdirectory("ac439-canon-");
        try
        {
            var spelledViaParentAndBack = Path.Combine(real.FullName, "..", real.Name);

            Assert.Equal(
                PhysicalResourceIdentity.Canonicalize(real.FullName),
                PhysicalResourceIdentity.Canonicalize(spelledViaParentAndBack));
        }
        finally
        {
            real.Delete();
        }
    }

    /// <summary>
    /// The case AC-439 names explicitly: a worktree reached through a symlink is the same physical thing as the real
    /// path underneath it. This is the guard that a plain <c>Path.GetFullPath</c> alone (no
    /// <see cref="File.ResolveLinkTarget"/>) would not satisfy — removing the resolve call collapses this test back
    /// to two different strings.
    /// </summary>
    [Fact]
    public void Canonicalize_ASymlinkToADirectory_ResolvesToTheSameIdentityAsTheRealPath()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Directory.CreateTempSubdirectory("ac439-canon-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real-worktree"));
            var symlinkPath = Path.Combine(root.FullName, "linked-worktree");
            Directory.CreateSymbolicLink(symlinkPath, real.FullName);

            Assert.Equal(
                PhysicalResourceIdentity.Canonicalize(real.FullName),
                PhysicalResourceIdentity.Canonicalize(symlinkPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Canonicalize_TwoDifferentExistingDirectories_StayDifferent()
    {
        var root = Directory.CreateTempSubdirectory("ac439-canon-");
        try
        {
            var first = Directory.CreateDirectory(Path.Combine(root.FullName, "worktree-a"));
            var second = Directory.CreateDirectory(Path.Combine(root.FullName, "worktree-b"));

            Assert.NotEqual(
                PhysicalResourceIdentity.Canonicalize(first.FullName),
                PhysicalResourceIdentity.Canonicalize(second.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
