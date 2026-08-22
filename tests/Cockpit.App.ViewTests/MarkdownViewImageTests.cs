using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Cockpit.App.Views;
using Cockpit.Core.Markdown;

namespace Cockpit.App.ViewTests;

// AC-1033 gave the shared parser an image block, and the chat uses that parser knowing nothing about it.
// These pin the transcript's side: a pasted image link keeps rendering as the link it always was.
[Collection("avalonia")]
public sealed class MarkdownViewImageTests
{
    [Fact]
    public void WithoutARenderer_AnImageLineStillShowsItsClickableLink() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView { Markdown = "![a screenshot](https://example.invalid/x.png)" };
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var text = Assert.IsAssignableFrom<SelectableTextBlock>(
                Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));

            Assert.Equal("!a screenshot", string.Concat(text.Inlines!.OfType<Run>().Select(run => run.Text)));
        }
        finally
        {
            window.Close();
        }
    });

    // The claim that actually says "the chat did not change": the tree drawn for an image line is the one the
    // inline parser produces for that same text, which is what the paragraph path built before this block kind
    // existed.
    [Fact]
    public void WithoutARenderer_AnImageLineRendersWhatTheInlineParserWouldHaveProduced() => HeadlessAvalonia.Run(() =>
    {
        const string Source = "![alt](https://example.invalid/x.png)";
        var view = new MarkdownView { Markdown = Source };
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            var expected = string.Concat(MarkdownParser.ParseInlines(Source).Select(inline => inline.Text));

            Assert.Equal(expected, _Runs(view));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void ImageLineIsNotSilentlyBlank() => HeadlessAvalonia.Run(() =>
    {
        var view = new MarkdownView { Markdown = "![alt](images/x.png)" };
        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            Assert.NotEmpty(_Runs(view));
        }
        finally
        {
            window.Close();
        }
    });

    // The knowledge base's side: a renderer takes over and gets the reference the author wrote, so it can
    // decide for itself whether it is something shipped with the page or something it refuses to fetch.
    [Fact]
    public void WithARenderer_TheBlockIsHandedOverWithItsReferenceIntact() => HeadlessAvalonia.Run(() =>
    {
        MarkdownBlock? seen = null;
        var view = new MarkdownView
        {
            ImageRenderer = block =>
            {
                seen = block;
                return new Border { Height = 10 };
            },
        };
        view.Markdown = "![Privileged intents](images/intents.png)";

        var window = new Window { Content = view, Width = 400, Height = 200 };
        window.Show();
        try
        {
            Assert.Equal("images/intents.png", seen?.ImageSource);
            Assert.Equal("Privileged intents", seen?.ImageAlt);
            Assert.IsType<Border>(Assert.Single(Assert.IsType<StackPanel>(view.Content).Children));
        }
        finally
        {
            window.Close();
        }
    });

    private static string _Runs(MarkdownView view) =>
        string.Concat(Assert.IsType<StackPanel>(view.Content).Children
            .OfType<SelectableTextBlock>()
            .SelectMany(text => text.Inlines!.OfType<Run>())
            .Select(run => run.Text));
}
