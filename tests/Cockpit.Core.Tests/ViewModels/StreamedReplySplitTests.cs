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

    [Fact]
    public void AReplySplitAcrossRows_HasOneTopTimestampRow()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        _Stream(vm, Reply);

        Assert.True(vm.Transcript.Count > 1, "the reply must split for this test to exercise continuation rows");
        Assert.Single(vm.Transcript, entry => entry.IsTopTimestampRow);
        Assert.True(vm.Transcript[0].IsTopTimestampRow);
    }

    /// <summary>An inline fence marker is prose, not an unclosed code block.</summary>
    [Fact]
    public void AReplyWithAnInlineFenceMarker_SplitsAndStillKeepsEveryCharacter()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        var stray = "Here is a fence marker ``` on its own in prose.\n\n" + Reply;
        _Stream(vm, stray);

        Assert.True(vm.Transcript.Count > 1, "the inline marker must not disable later block boundaries");
        Assert.Equal(stray, vm.Transcript[^1].ReplyTextWithImageSuffix);
    }

    [Theory]
    [InlineData("```")]
    [InlineData("~~~")]
    public void AReplyWithAFencedCodeBlock_KeepsItsBlankLinesTogether(string fence)
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        var codeBlock = $"{fence}csharp\nvar first = 1;\n\nvar second = 2;\n{fence}\n\n";
        var reply = codeBlock + Reply;
        _Stream(vm, reply);

        Assert.Equal(codeBlock, vm.Transcript[0].Text);
        Assert.True(vm.Transcript.Count > 1);
        Assert.Equal(reply, vm.Transcript[^1].ReplyTextWithImageSuffix);
    }

    [Fact]
    public void ABacktickFence_IsNotClosedByATildeFence()
    {
        var vm = new SessionViewModel();
        vm.Transcript.Clear();

        var codeBlock = "```csharp\nvar first = 1;\n~~~\n\nvar second = 2;\n```\n\n";
        var reply = codeBlock + Reply;
        _Stream(vm, reply);

        Assert.Equal(codeBlock, vm.Transcript[0].Text);
        Assert.Equal(reply, vm.Transcript[^1].ReplyTextWithImageSuffix);
    }
}
