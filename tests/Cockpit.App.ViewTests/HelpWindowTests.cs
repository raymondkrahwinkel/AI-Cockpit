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

    // One page carrying all three cases, asserted together, because the window renders a whole article at
    // once: exactly one picture is drawn — the one that shipped beside the page — and the other two references
    // account for themselves in words.
    [Fact]
    public void OnlyThePictureThatShippedWithThePageIsDrawn() => HeadlessAvalonia.Run(() =>
        _Assert("fixtures/pictures", content =>
        {
            Assert.Single(_Descendants<Image>(content));
            Assert.Contains(_Text(content), text => text.Contains("The setting you are looking for", StringComparison.Ordinal));
        }));

    // The boundary this whole surface rests on: opening a page from a plugin you did not write must not be the
    // moment a stranger's server learns the operator exists. Nothing is drawn that could have fetched it, and
    // the refusal is said out loud rather than left as a gap.
    [Fact]
    public void APictureFromSomewhereElseIsRefusedAndSaidSo() => HeadlessAvalonia.Run(() =>
        _Assert("fixtures/pictures", content =>
        {
            Assert.Contains(_Text(content), text => text.Contains("External picture not loaded", StringComparison.Ordinal));
            Assert.Contains(_Text(content), text => text.Contains("example.invalid", StringComparison.Ordinal));
        }));

    [Fact]
    public void APictureThatWasNotShippedSaysSoRatherThanLeavingAGap() => HeadlessAvalonia.Run(() =>
        _Assert("fixtures/pictures", content =>
            Assert.Contains(_Text(content), text => text.Contains("not shipped with it", StringComparison.Ordinal))));

    // A reference that leads nowhere fails visibly: the plugin may be uninstalled or the section renamed, and
    // either way the operator clicked something that promised an answer.
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

    // A page from someone else reads and is styled exactly like ours; only who wrote it is shown differently.
    [Fact]
    public void APageFromSomeoneElseSaysWhoWroteIt() => HeadlessAvalonia.Run(() =>
        _Assert("fixtures/pictures", content =>
        {
            Assert.Contains(_Text(content), text => text.Contains("third-party", StringComparison.Ordinal));
            Assert.Contains(_Text(content), text => text.Contains("Someone Else", StringComparison.Ordinal));
        }));

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
