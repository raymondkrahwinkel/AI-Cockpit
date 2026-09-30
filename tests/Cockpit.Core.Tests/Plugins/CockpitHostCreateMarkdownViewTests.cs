using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Cockpit.App.Views;
using Cockpit.Plugins.Abstractions.UI;

namespace Cockpit.Core.Tests.Plugins;

/// <summary>
/// <see cref="ICockpitUiHost.CreateMarkdownView"/> (AC-296): the seam a plugin uses to render an issue description or
/// comment through the cockpit's own markdown look instead of showing raw "##"/"**" syntax. The real host renders
/// through <see cref="MarkdownView"/> rather than a second parser.
/// </summary>
public class CockpitHostCreateMarkdownViewTests
{
    [Fact]
    public void HostImplementation_RendersMarkdownInsteadOfLeavingTheRawSyntaxInTheOutput()
    {
        var host = _BuildHost();

        var view = host.CreateMarkdownView("## Heading\n\nBody text.");

        Assert.IsType<MarkdownView>(view);
        var texts = _CollectText((Control)view).ToList();
        Assert.Contains("Heading", texts);
        Assert.Contains("Body text.", texts);
        Assert.DoesNotContain(texts, text => text.Contains("##"));
    }

    [Fact]
    public void ADescriptionWithinTheBudget_IsRenderedWhole()
    {
        var body = string.Join("\n\n", Enumerable.Range(1, 200).Select(paragraph => $"Paragraph {paragraph}."));

        var view = _BuildHost().CreateMarkdownView(body);

        var texts = _CollectText((Control)view).ToList();
        Assert.Contains("Paragraph 200.", texts);
        Assert.DoesNotContain(texts, text => text.Contains("truncated"));
    }

    /// <summary>
    /// The body is a third party's — a GitHub issue may hold 65 536 characters, and rendering it builds a control per
    /// cell and per line while the operator waits (AC-303). Cut it, and say so: silently showing two thirds of a
    /// description in a panel whose next button injects that text into an agent is worse than the delay.
    /// </summary>
    [Fact]
    public void ADescriptionPastTheBudget_IsCutAndSaysSo()
    {
        var body = new string('x', 100_000);

        var view = _BuildHost().CreateMarkdownView(body);

        var texts = _CollectText((Control)view).ToList();
        Assert.True(texts.Sum(text => text.Length) < 70_000);
        Assert.Contains(texts, text => text.Contains("truncated"));
    }

    private static IEnumerable<string> _CollectText(Control? control)
    {
        switch (control)
        {
            case null:
                yield break;
            case SelectableTextBlock textBlock:
                yield return string.Concat((textBlock.Inlines ?? []).OfType<Run>().Select(run => run.Text));
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    foreach (var text in _CollectText(child))
                    {
                        yield return text;
                    }
                }

                break;
            case Border border:
                foreach (var text in _CollectText(border.Child))
                {
                    yield return text;
                }

                break;
            case ContentControl contentControl:
                foreach (var text in _CollectText(contentControl.Content as Control))
                {
                    yield return text;
                }

                break;
        }
    }

    private static ICockpitUiHost _BuildHost() => TestUiHost.Create();
}
