using Cockpit.App.ViewModels;
using Cockpit.Core.Worktrees;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// The managed-worktrees row's owner label (AC-520): whether the owning session's name is known should never
/// change whether the row reads "in use" or "session gone" — only whether that reason names the owner.
/// </summary>
public class ManagedWorktreeRowViewModelTests
{
    [Fact]
    public void OwnerLabel_LiveAndNameKnown_NamesTheOwner()
    {
        var row = _Row(isOwnerLive: true, ownerName: "AC-520");

        Assert.Equal("in use · claimed by AC-520", row.OwnerLabel);
    }

    [Fact]
    public void OwnerLabel_GoneAndNameKnown_NamesTheOwner()
    {
        var row = _Row(isOwnerLive: false, ownerName: "AC-520");

        Assert.Equal("session gone · was AC-520", row.OwnerLabel);
    }

    [Fact]
    public void OwnerLabel_LiveAndNameUnknown_ReadsAsBefore()
    {
        var row = _Row(isOwnerLive: true, ownerName: null);

        Assert.Equal("in use · claimed by a pane", row.OwnerLabel);
    }

    [Fact]
    public void OwnerLabel_GoneAndNameUnknown_ReadsAsBefore()
    {
        var row = _Row(isOwnerLive: false, ownerName: null);

        Assert.Equal("session gone", row.OwnerLabel);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void OwnerLabel_BlankName_CountsAsUnknown(string blank)
    {
        var row = _Row(isOwnerLive: true, ownerName: blank);

        Assert.Equal("in use · claimed by a pane", row.OwnerLabel);
    }

    /// <summary>A newline in an agent-suggested name must not carry through to a label the dialog renders on one line.</summary>
    [Fact]
    public void OwnerLabel_NameWithNewlines_IsFlattenedToOneLine()
    {
        var row = _Row(isOwnerLive: true, ownerName: "line one\nline two\r\nline three");

        Assert.DoesNotContain('\n', row.OwnerLabel);
        Assert.DoesNotContain('\r', row.OwnerLabel);
        // Each control character folds to its own space (WorktreeTools._SingleLine's precedent) rather than
        // collapsing runs of them — "\r\n" is two characters, so it becomes two spaces.
        Assert.Equal("in use · claimed by line one line two  line three", row.OwnerLabel);
    }

    /// <summary>
    /// The Unicode line/paragraph separators git's own ref-name check does not reject — same reasoning as
    /// <c>WorktreeTools._SingleLine</c> — must be folded the same way a plain newline is.
    /// </summary>
    [Fact]
    public void OwnerLabel_NameWithUnicodeLineSeparators_IsFlattenedToOneLine()
    {
        var row = _Row(isOwnerLive: true, ownerName: "before\u2028after\u2029and\u0085more");

        Assert.Equal("in use · claimed by before after and more", row.OwnerLabel);
    }

    [Fact]
    public void OwnerLabel_NameLongerThanTheCeiling_IsCutWithAnEllipsis()
    {
        var longName = new string('x', 80);

        var row = _Row(isOwnerLive: true, ownerName: longName);

        Assert.Equal($"in use · claimed by {new string('x', 40)}…", row.OwnerLabel);
    }

    private static ManagedWorktreeRowViewModel _Row(bool isOwnerLive, string? ownerName, bool hasOpenRestoreOffer = false)
    {
        var record = new WorktreeRecord("session", "/repo", "/state/worktrees/ab/cockpit-x", "cockpit/x", "0123456789abcdef0123456789abcdef01234567", DateTimeOffset.UtcNow);
        var status = new WorktreeStatus(record, Exists: true, HasUncommittedChanges: false, StrandableCommits: 0);

        return new ManagedWorktreeRowViewModel(status, isOwnerLive, ownerName, hasOpenRestoreOffer);
    }
}
