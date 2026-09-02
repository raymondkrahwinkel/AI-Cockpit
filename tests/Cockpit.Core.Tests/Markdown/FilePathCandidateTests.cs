using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

// The vorm filter only — see the class doc on FilePathCandidate for why it cannot tell `Theme.axaml` from
// `System.Text.Json` and does not try to.
public class FilePathCandidateTests
{
    /// <summary>
    /// Every shape the filter lets through. The last two are on-form identical to each other and to an ordinary
    /// dotted identifier — `System.Text.Json` cannot be told from `Theme.axaml` here, and the filter does not try:
    /// that split happens on disk in FilePathResolver, which is exactly why this cheap first pass exists.
    /// </summary>
    [Theory]
    [InlineData(@"C:\Users\raymo\AppData\Local\Temp\chip\all.png")]
    [InlineData(@"\\host\share\file.txt")]
    [InlineData("/home/raymond/repo/file.cs")]
    [InlineData(@"C:\Program Files\x.txt")]
    [InlineData("src/Cockpit.App/Views/MarkdownView.cs")]
    [InlineData("Theme.axaml")]
    [InlineData("System.Text.Json")]
    public void CandidateForms_AreAccepted(string text)
    {
        Assert.True(FilePathCandidate.TryParse(text, out var path, out var line));
        Assert.Equal(text, path);
        Assert.Null(line);
    }

    [Theory]
    [InlineData("--warnaserror")] // no dot, no separator
    [InlineData("ToggleListeningModeCommand")] // no dot, no separator
    [InlineData("git stash -u")] // space, no separator
    [InlineData("CheckBox.Switch")] // ".Switch" is 6 characters, over the 5-character cap
    [InlineData("https://example.com/path")] // a scheme, not a path
    [InlineData("first\nsecond.cs")] // a line break
    [InlineData("")]
    [InlineData("   ")]
    public void NonCandidates_AreRejected(string text)
    {
        Assert.False(FilePathCandidate.TryParse(text, out _, out _));
    }

    // A trailing :line is extracted and stripped; a :line:column keeps only the line, because that is all the
    // editor hand-off carries.
    [Theory]
    [InlineData("src/Cockpit.App/Views/MarkdownView.cs:594")]
    [InlineData("src/Cockpit.App/Views/MarkdownView.cs:594:12")]
    public void TrailingLineNumber_IsExtractedAndStripped(string text)
    {
        Assert.True(FilePathCandidate.TryParse(text, out var path, out var line));
        Assert.Equal("src/Cockpit.App/Views/MarkdownView.cs", path);
        Assert.Equal(594, line);
    }
}
