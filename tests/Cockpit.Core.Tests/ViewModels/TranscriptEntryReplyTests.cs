using Cockpit.App.ViewModels;

namespace Cockpit.Core.Tests.ViewModels;

// AC-935: a row's reply relation — the citation it shows, and the "answered" marker on the row it targets.
public class TranscriptEntryReplyTests
{
    [Fact]
    public void ARowWithoutAReplyTarget_HasNoCitation()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "fix the layout bug");

        Assert.False(entry.HasReplyTo);
        Assert.Equal(string.Empty, entry.ReplyExcerpt);
    }

    [Fact]
    public void ARowWithAReplyTarget_ExcerptsTheTargetsOwnText()
    {
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "please check the build output");
        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "looks fine to me") { ReplyTo = target };

        Assert.True(reply.HasReplyTo);
        Assert.Equal("please check the build output", reply.ReplyExcerpt);
    }

    [Fact]
    public void ARowThatHasNotBeenRepliedTo_HasNoAnsweredMarker()
    {
        var entry = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "question");

        Assert.False(entry.HasReplies);
        Assert.Null(entry.LatestReply);
    }

    [Fact]
    public void ARowThatWasRepliedTo_TracksItsLatestReply()
    {
        var target = new TranscriptEntryViewModel(TranscriptEntryKind.AssistantText, "question");
        var reply = new TranscriptEntryViewModel(TranscriptEntryKind.UserText, "answer") { ReplyTo = target };

        target.LatestReply = reply;

        Assert.True(target.HasReplies);
        Assert.Same(reply, target.LatestReply);
    }

    // Newlines/tabs collapse to a single line and quotes are swapped so the excerpt can never close the wire
    // format's own quoting (`[reply to "<excerpt>"]: <input>`).
    [Fact]
    public void BuildReplyExcerpt_CollapsesWhitespaceAndSwapsQuotes()
    {
        var excerpt = TranscriptEntryViewModel.BuildReplyExcerpt("line one\nline \"two\"\tline three");

        Assert.Equal("line one line 'two' line three", excerpt);
    }

    // Capped so quoting a long status report does not double a reply's token cost for no identification benefit.
    [Fact]
    public void BuildReplyExcerpt_TruncatesLongTextWithAnEllipsis()
    {
        var excerpt = TranscriptEntryViewModel.BuildReplyExcerpt(new string('a', 250));

        Assert.Equal(201, excerpt.Length);
        Assert.EndsWith("…", excerpt);
    }

    [Fact]
    public void BuildReplyExcerpt_ShortTextIsReturnedAsIs()
    {
        var excerpt = TranscriptEntryViewModel.BuildReplyExcerpt("short message");

        Assert.Equal("short message", excerpt);
    }
}
