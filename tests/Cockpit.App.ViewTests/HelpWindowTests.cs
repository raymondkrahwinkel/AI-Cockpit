using Avalonia.Controls;
using Avalonia.VisualTree;
using Cockpit.App.Services;
using Cockpit.App.Views;
using Cockpit.Core.Help;

namespace Cockpit.App.ViewTests;

// AC-1033's rendering rules on the window itself. The fixtures under this project's own `Docs` folder are
// embedded by the same SDK targets a plugin's are, so this runs the real path rather than a stand-in.
[Collection("avalonia")]
public sealed class HelpWindowTests
{
    private static readonly HelpOwner Fixtures = new("fixtures", "Fixtures", "Someone Else");

    private static HelpService _Help() => new([
        new HelpDocumentSource(Fixtures, typeof(HelpWindowTests).Assembly),
    ]);

    // One page, one render, every claim the fixture article makes. Five facts stood here, each opening its own
    // HelpWindow on this same page for one phrase — the same exercise five times, as the first one's own comment
    // already said ("the window renders a whole article at once"). A missing phrase is named by the assertion.
    [Fact]
    public void TheFixtureArticle_RendersItsPictureAndAccountsForEveryOtherReference() => HeadlessAvalonia.Run(() =>
        _Assert("fixtures/pictures", content =>
        {
            // Exactly one picture is drawn — the one that shipped beside the page.
            Assert.Single(_Descendants<Image>(content));
            Assert.Contains(_Text(content), text => text.Contains("The setting you are looking for", StringComparison.Ordinal));

            // The boundary this whole surface rests on: opening a page from a plugin you did not write must not be
            // the moment a stranger's server learns the operator exists. Nothing is drawn that could have fetched
            // it, and the refusal is said out loud rather than left as a gap.
            Assert.Contains(_Text(content), text => text.Contains("External picture not loaded", StringComparison.Ordinal));
            Assert.Contains(_Text(content), text => text.Contains("example.invalid", StringComparison.Ordinal));

            // A picture that was never shipped says so rather than leaving a gap.
            Assert.Contains(_Text(content), text => text.Contains("not shipped with it", StringComparison.Ordinal));

            // A page from someone else reads and is styled exactly like ours; only who wrote it is shown differently.
            Assert.Contains(_Text(content), text => text.Contains("third-party", StringComparison.Ordinal));
            Assert.Contains(_Text(content), text => text.Contains("Someone Else", StringComparison.Ordinal));

            // The branch the core does not fill: one entry named after the plugin, with its pages under it.
            // Asserted here rather than in a screenshot scene, because a scene would need a plugin assembly the
            // Release build never loads — which is how a green local run and a red CI run came apart once already.
            Assert.Contains(_Text(content), text => text.Contains("PLUGINS", StringComparison.Ordinal));
            Assert.Contains(_Text(content), text => text.Contains("Fixtures", StringComparison.Ordinal));
        }));

    // A reference that leads nowhere fails visibly: the plugin may be uninstalled or the section renamed, and
    // either way the operator clicked something that promised an answer. Its own test because it is a different
    // page — an address that resolves to nothing at all, not a reference inside a page that renders.
    [Fact]
    public void AReferenceThatLeadsNowhereFailsWhereItCanBeSeen() => HeadlessAvalonia.Run(() =>
    {
        var window = new HelpWindow(_Help());
        window.NavigateTo(new HelpAddress("slack", "interactivity"));
        window.Show();
        try
        {
            Assert.Contains(_Text(window), text => text.Contains("This page is not here", StringComparison.Ordinal));
        }
        finally
        {
            window.Close();
        }
    });

    // AC-1042: the three plugin guides ship from `docs/plugins/` rather than from a `Docs` folder of the app's
    // own, which is one line of MSBuild away from silently shipping nothing at all.
    [Theory]
    [InlineData("PLUGIN-SDK")]
    [InlineData("API-REFERENCE")]
    [InlineData("AUTOMATED-PUBLISH")]
    public void TheGuidesInDocsPluginsShipInsideTheApp(string key)
    {
        var articles = HelpDocumentScanner.Scan(typeof(HelpWindow).Assembly, HelpOwner.Core);
        var article = Assert.Single(articles, candidate => candidate.Key == key);

        Assert.Equal(HelpCategory.ExtendingCockpit, article.Category);
        Assert.NotEmpty(article.Sections);
    }

    private static void _Assert(string article, Action<Control> assert)
    {
        var window = new HelpWindow(_Help());
        window.NavigateTo(new HelpAddress(article));
        window.Show();
        try
        {
            assert(window);
        }
        finally
        {
            window.Close();
        }
    }

    private static IEnumerable<T> _Descendants<T>(Control root) where T : Control =>
        root.GetVisualDescendants().OfType<T>();

    private static IEnumerable<string> _Text(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text ?? string.Empty);
}
