using Cockpit.App.ViewModels;
using Cockpit.Core.Sessions;

namespace Cockpit.Core.Tests.ViewModels;

/// <summary>
/// AC-1238: a streamed reply is delivered as one row per finished markdown block, so the row growing under the
/// virtualising panel is always the last and smallest one. Two promises come with that, and the comments in
/// <c>SessionViewModel</c> make both of them in prose — these are the tests that keep them.
/// </summary>
public class StreamedReplySplitTests
{
    private const string Reply =
        "## What I found\n\nTwo faults that multiply each other.\n\n"
        + "- `release.yml` builds only the desktop client\n- a Dockerfile but no workflow\n\n"
        + "So the last one is what to fix first.\n\n";

    /// <summary>Splits the source the way a provider does: at no boundary in particular.</summary>
    private static void _Stream(SessionViewModel vm, string text, int chunk = 7)
    {
        for (var at = 0; at < text.Length; at += chunk)
        {
            vm.Apply(new AssistantTextDelta
            {
                SessionId = "S1",
                BlockIndex = 0,
                Text = text.Substring(at, Math.Min(chunk, text.Length - at)),
            });
        }
    }

    /// <summary>
    /// The split keeps the blank line that ended each block on the row that ended it, so putting the rows back
    /// together needs no separator and returns the reply byte for byte. That is what "copy this reply" hands over
    /// and what read-aloud speaks, and both would be subtly wrong if a boundary ever moved a character.
    /// </summary>
    [Fact]
    public void AReplySplitAcrossRows_CopiesBackExactlyWhatWasStreamed()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        _Stream(vm, Reply);

        Assert.True(
            vm.Transcript.Count > 1,
            $"the reply landed on {vm.Transcript.Count} row(s): it was never split, so this proves nothing about "
            + "putting a split one back together");
        Assert.Equal(Reply, vm.Transcript[^1].ReplyTextWithImageSuffix);
        Assert.Equal(Reply, vm.Transcript[0].ReplyTextWithImageSuffix);
    }

    /// <summary>
    /// The boundary rule counts code fences so a blank line inside one does not end a block. It counts them by
    /// pairs, so an unbalanced fence — a stray ``` in prose, or a reply fenced with ~~~ instead — leaves the
    /// parity stuck and the rest of that reply on one row. That is a silent fall back to how this behaved before
    /// AC-1238: the flicker returns for that reply, and nothing says so. What must not happen is losing text, so
    /// that is what this pins; the row count is asserted only to show the fall back is what happened.
    /// </summary>
    [Fact]
    public void AReplyWithAnUnbalancedFence_FallsBackToOneRowAndStillKeepsEveryCharacter()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        var stray = "Here is a fence marker ``` on its own in prose.\n\n" + Reply;
        _Stream(vm, stray);

        // One row: the stray marker sits in the first block, so the very first blank line already reads as inside
        // a fence and nothing after it ever ends a block either.
        Assert.Equal(1, vm.Transcript.Count);
        Assert.Equal(stray, vm.Transcript[^1].ReplyTextWithImageSuffix);
    }
}
