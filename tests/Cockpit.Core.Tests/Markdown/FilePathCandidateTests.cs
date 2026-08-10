using Cockpit.Core.Markdown;

namespace Cockpit.Core.Tests.Markdown;

// The vorm filter only — see the class doc on FilePathCandidate for why it cannot tell `Theme.axaml` from
// `System.Text.Json` and does not try to.
public class FilePathCandidateTests
{
    [Theory]
    [InlineData(@"C:\Users\raymo\AppData\Local\Temp\chip\all.png")]
    [InlineData(@"\\host\share\file.txt")]
    [InlineData("/home/raymond/repo/file.cs")]
    public void AbsoluteForms_AreAccepted(string text)
    {
        Assert.True(FilePathCandidate.TryParse(text, out var path, out var line));
        Assert.Equal(text, path);
        Assert.Null(line);
    }

    [Fact]
    public void RelativePathWithSeparator_IsAccepted()
    {
        Assert.True(FilePathCandidate.TryParse("src/Cockpit.App/Views/MarkdownView.cs", out var path, out var line));
        Assert.Equal("src/Cockpit.App/Views/MarkdownView.cs", path);
        Assert.Null(line);
    }

    [Fact]
    public void ShortExtensionWithoutSeparator_IsAccepted()
    {
        // The vorm filter cannot tell this from `System.Text.Json` — that split happens on disk, not here.
        Assert.True(FilePathCandidate.TryParse("Theme.axaml", out var path, out _));
        Assert.Equal("Theme.axaml", path);
    }

    [Fact]
    public void DottedIdentifierWithShortTail_PassesTheFormFilterToo()
    {
        // `System.Text.Json` is on-form identical to `Theme.axaml` — only `FilePathResolver`'s disk probe
        // tells them apart, which is exactly why this filter exists as its own, cheap first pass.
        Assert.True(FilePathCandidate.TryParse("System.Text.Json", out var path, out _));
        Assert.Equal("System.Text.Json", path);
    }

    [Theory]
    [InlineData("--warnaserror")] // no dot, no separator
    [InlineData("ToggleListeningModeCommand")] // no dot, no separator
    [InlineData("git stash -u")] // space, no separator
    [InlineData("CheckBox.Switch")] // ".Switch" is 6 characters, over the 5-character cap
    [InlineData("")]
    [InlineData("   ")]
    public void NonCandidates_AreRejected(string text)
    {
        Assert.False(FilePathCandidate.TryParse(text, out _, out _));
    }

    [Fact]
    public void TrailingLineNumber_IsExtractedAndStripped()
    {
        Assert.True(FilePathCandidate.TryParse("src/Cockpit.App/Views/MarkdownView.cs:594", out var path, out var line));
        Assert.Equal("src/Cockpit.App/Views/MarkdownView.cs", path);
        Assert.Equal(594, line);
    }

    [Fact]
    public void TrailingLineAndColumn_KeepsOnlyTheLine()
    {
        Assert.True(FilePathCandidate.TryParse("src/Cockpit.App/Views/MarkdownView.cs:594:12", out var path, out var line));
        Assert.Equal("src/Cockpit.App/Views/MarkdownView.cs", path);
        Assert.Equal(594, line);
    }

    [Fact]
    public void SpaceInsideAnAbsolutePath_IsAllowed()
    {
        Assert.True(FilePathCandidate.TryParse(@"C:\Program Files\x.txt", out var path, out _));
        Assert.Equal(@"C:\Program Files\x.txt", path);
    }

    [Fact]
    public void UrlWithScheme_IsRejected()
    {
        Assert.False(FilePathCandidate.TryParse("https://example.com/path", out _, out _));
    }

    [Fact]
    public void TextContainingALineBreak_IsRejected()
    {
        Assert.False(FilePathCandidate.TryParse("first\nsecond.cs", out _, out _));
    }
}
