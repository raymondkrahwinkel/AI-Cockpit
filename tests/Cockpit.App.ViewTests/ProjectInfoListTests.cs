using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cockpit.App.Controls;
using Cockpit.Core.Projects;
using FluentAssertions;

namespace Cockpit.App.ViewTests;

/// <summary>
/// The block that shows a project's own information (AC-295) on its card and in the projects window. Two things can
/// only be known by rendering it: that a web address is drawn as something followable while an ordinary value is not,
/// and that a long value trims inside the card rather than making the card wider than its column.
/// </summary>
[Collection("avalonia")]
public class ProjectInfoListTests
{
    /// <summary>The width of a project card in the overview, which is the narrowest place this block has to fit.</summary>
    private const double CardWidth = 278d;

    [Fact]
    public void AWebAddress_IsFollowable_AndAnOrdinaryValueIsNot() => HeadlessAvalonia.Run(() =>
    {
        var window = _Host(
            new ProjectInfoField("Repository", "https://github.com/example/repo"),
            new ProjectInfoField("Customer", "Acme BV — ask for Marcel"));

        var buttons = window.GetVisualDescendants().OfType<Button>().Where(button => button.IsVisible).ToList();
        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsVisible)
            .Select(block => block.Text)
            .ToList();

        window.Close();

        buttons.Should().ContainSingle("only the web address may look clickable");
        texts.Should().Contain("Acme BV — ask for Marcel", "an ordinary value is still shown, just not as a link");
    });

    [Fact]
    public void AWebAddress_IsDrawnAsALinkRatherThanOrdinaryText() => HeadlessAvalonia.Run(() =>
    {
        // Setting the accent on the button alone rendered the link in ordinary text colour: Foreground does not
        // inherit into the TextBlock inside a Button's content. It looked exactly like the label beside it.
        var window = _Host(new ProjectInfoField("Repository", "https://github.com/example/repo"));

        var link = window.GetVisualDescendants().OfType<TextBlock>()
            .First(block => block.Text == "https://github.com/example/repo");
        var accent = (IBrush?)Application.Current?.FindResource("CockpitAccentBrush");
        var foreground = link.Foreground;
        var decorations = link.TextDecorations;

        window.Close();

        decorations.Should().NotBeNullOrEmpty("a link has to be recognisable as one before it is hovered");
        foreground?.ToString().Should().Be(accent?.ToString(), "the accent is this app's link colour");
    });

    [Fact]
    public void ARowWithoutALabel_ShowsItsValueAlone() => HeadlessAvalonia.Run(() =>
    {
        var window = _Host(new ProjectInfoField("", "https://example.test/pasted"));

        var texts = window.GetVisualDescendants().OfType<TextBlock>()
            .Where(block => block.IsVisible)
            .Select(block => block.Text)
            .ToList();

        window.Close();

        texts.Should().ContainSingle().Which.Should().Be("https://example.test/pasted",
            "pasting a link without inventing a label first is the fastest thing the editor can do");
    });

    [Fact]
    public void AVeryLongValue_TrimsInsteadOfWideningTheCard() => HeadlessAvalonia.Run(() =>
    {
        var window = _Host(new ProjectInfoField(
            "Repository",
            "https://github.example.test/an-organisation-with-a-long-name/a-repository-with-an-even-longer-name/tree/main/src"));

        var overflowing = window.GetVisualDescendants().OfType<Control>()
            .Select(control => (control.TranslatePoint(new Point(control.Bounds.Width, 0), window) ?? default).X)
            .Where(right => right > CardWidth + 1)
            .ToList();

        window.Close();

        overflowing.Should().BeEmpty("a card is a fixed width, so a long link has to trim rather than push past it");
    });

    private static Window _Host(params ProjectInfoField[] fields)
    {
        var window = new Window
        {
            Width = CardWidth,
            Height = 200,
            Content = new ProjectInfoList { Fields = fields },
        };

        window.Show();
        window.UpdateLayout();

        return window;
    }
}
